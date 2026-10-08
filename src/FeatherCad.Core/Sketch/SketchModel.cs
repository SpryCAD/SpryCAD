using System.Text.Json.Serialization;
using FeatherCad.Kernel;

namespace FeatherCad.Core.Sketching;

public sealed class SkPoint
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Fixed points (the sketch origin, user-fixed points) are not solver variables.</summary>
    public bool Fixed { get; set; }
    [JsonIgnore] public Vec2 P { get => new(X, Y); set { X = value.X; Y = value.Y; } }
    public SkPoint Clone() => (SkPoint)MemberwiseClone();
}

/// <summary>
/// Link from a sketch entity to model geometry (an edge or one of its end vertices). The entity is
/// re-projected onto the sketch plane on every rebuild, so dimensions to model edges stay associative.
/// </summary>
public sealed class SkExternal
{
    public FeatherCad.Core.Model.EdgeRef Edge { get; set; } = new();
    /// <summary>True: the end vertex of <see cref="Edge"/> nearest to <see cref="Hint"/>.</summary>
    public bool Vertex { get; set; }
    public Vec3 Hint { get; set; }

    public bool SameAs(SkExternal o) =>
        Vertex == o.Vertex && Vec3.Distance(Hint, o.Hint) < 1e-6 &&
        ((Edge.A == o.Edge.A && Edge.B == o.Edge.B) || (Edge.A == o.Edge.B && Edge.B == o.Edge.A));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SkLine), "line")]
[JsonDerivedType(typeof(SkCircle), "circle")]
[JsonDerivedType(typeof(SkArc), "arc")]
[JsonDerivedType(typeof(SkPointEntity), "point")]
public abstract class SkEntity
{
    public int Id { get; set; }
    public bool Construction { get; set; }
    /// <summary>Reference to model geometry (fixed, not part of profiles).</summary>
    public SkExternal? External { get; set; }
    [JsonIgnore] public bool IsReference => External != null;
    public abstract IEnumerable<int> PointIds { get; }
    public abstract SkEntity Clone();
    public abstract void RemapPoint(int from, int to);
}

public sealed class SkLine : SkEntity
{
    public int P1 { get; set; }
    public int P2 { get; set; }
    public override IEnumerable<int> PointIds { get { yield return P1; yield return P2; } }
    public override SkEntity Clone() => (SkEntity)MemberwiseClone();
    public override void RemapPoint(int from, int to) { if (P1 == from) P1 = to; if (P2 == from) P2 = to; }
}

public sealed class SkCircle : SkEntity
{
    public int Center { get; set; }
    public double R { get; set; }
    public override IEnumerable<int> PointIds { get { yield return Center; } }
    public override SkEntity Clone() => (SkEntity)MemberwiseClone();
    public override void RemapPoint(int from, int to) { if (Center == from) Center = to; }
}

/// <summary>Arc from Start to End counter-clockwise around Center.</summary>
public sealed class SkArc : SkEntity
{
    public int Center { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
    public override IEnumerable<int> PointIds { get { yield return Center; yield return Start; yield return End; } }
    public override SkEntity Clone() => (SkEntity)MemberwiseClone();
    public override void RemapPoint(int from, int to) { if (Center == from) Center = to; if (Start == from) Start = to; if (End == from) End = to; }
}

public sealed class SkPointEntity : SkEntity
{
    public int P { get; set; }
    public override IEnumerable<int> PointIds { get { yield return P; } }
    public override SkEntity Clone() => (SkEntity)MemberwiseClone();
    public override void RemapPoint(int from, int to) { if (P == from) P = to; }
}

public enum CType
{
    // geometric relations
    Horizontal, Vertical,           // line, or two points
    Parallel, Perpendicular, Collinear,
    Equal,                          // lines (length) or arcs/circles (radius)
    Tangent,                        // line+arc/circle, arc/circle+arc/circle
    PointOnLine, PointOnCircle, Midpoint,
    Concentric,
    Symmetric,                      // p1, p2, line
    Coincident,                     // p, p (normally points are merged instead)
    // dimensions
    Distance,                       // p,p | p,line | line(length)
    HDistance, VDistance,           // p,p
    Radius, Diameter,               // circle/arc
    Angle,                          // line,line
    Offset,                         // original, copy, original, copy, ... (Sides: the side of each copy); Value: the distance
}

public sealed class SkConstraint
{
    public int Id { get; set; }
    public CType Type { get; set; }
    /// <summary>Referenced point / entity ids (shared id space).</summary>
    public int[] Refs { get; set; } = Array.Empty<int>();
    /// <summary>Dimension expression (e.g. "25", "W/2").</summary>
    public string? Expr { get; set; }
    /// <summary>Current numeric value of the dimension (mm or degrees).</summary>
    public double Value { get; set; }
    /// <summary>Orientation sign captured at creation for signed dimensions/tangency variants.</summary>
    public int Sign { get; set; } = 1;
    /// <summary>Angle dimensions: bit 0/1 reverse the direction of the first/second line so the measured
    /// angle is the sector the dimension was placed in.</summary>
    public int Flip { get; set; }
    /// <summary>Reference (driven) dimension: displays the value but does not constrain.</summary>
    public bool Driven { get; set; }
    /// <summary>Offset: the side of each copy (+1 / -1; for a line, left of its direction; for an arc or circle, outward).</summary>
    public int[]? Sides { get; set; }
    /// <summary>Dimension label offset in sketch coordinates relative to the anchor.</summary>
    public double LabelX { get; set; }
    public double LabelY { get; set; }
    /// <summary>
    /// The user put the label where it is (clicked there when making the dimension, or dragged it): it stays there.
    /// Otherwise the editor lays it out (clear of the geometry and the other labels). Null in files from before
    /// this flag, where a label off its default spot (a nonzero offset) was placed by hand.
    /// </summary>
    public bool? LabelPlaced { get; set; }
    [JsonIgnore] public bool HasPlacedLabel => LabelPlaced ?? (LabelX != 0 || LabelY != 0);
    /// <summary>Optional user name (e.g. D1) usable in equations as D1@Sketch1.</summary>
    public string? Name { get; set; }

    [JsonIgnore] public bool IsDimension => Type is CType.Distance or CType.HDistance or CType.VDistance or CType.Radius or CType.Diameter or CType.Angle or CType.Offset;
    public SkConstraint Clone() { var c = (SkConstraint)MemberwiseClone(); c.Refs = (int[])Refs.Clone(); c.Sides = (int[]?)Sides?.Clone(); return c; }
}

public enum SketchStatus { UnderDefined, FullyDefined, OverDefined, Failed }

/// <summary>
/// A sketch fillet between two lines meeting at a corner. The original lines (<see cref="LineA"/>,
/// <see cref="LineB"/>) stay, ending at the corner, as construction geometry: the virtual sharp, so dimensions
/// to them and to the corner keep their meaning. The profile follows the trimmed lines
/// (<see cref="TrimA"/>, <see cref="TrimB"/>, collinear with the originals) and the tangent <see cref="Arc"/>.
/// Deleting the arc restores the lines (<see cref="Sketch.DeleteEntity"/>).
/// </summary>
public sealed class SkFillet
{
    public int Arc { get; set; }
    public int LineA { get; set; }
    public int LineB { get; set; }
    public int TrimA { get; set; }
    public int TrimB { get; set; }
    public SkFillet Clone() => (SkFillet)MemberwiseClone();
}

public sealed class Sketch
{
    public const int OriginId = 0;

    public List<SkPoint> Points { get; set; } = new() { new SkPoint { Id = OriginId, Fixed = true } };
    public List<SkEntity> Entities { get; set; } = new();
    public List<SkConstraint> Constraints { get; set; } = new();
    /// <summary>Sketch fillets (null when none, so sketches without them are saved as before).</summary>
    public List<SkFillet>? Fillets { get; set; }
    public int NextId { get; set; } = 1;

    public int NewId() => NextId++;

    public SkPoint? Point(int id) { foreach (var p in Points) if (p.Id == id) return p; return null; }
    public SkEntity? Entity(int id) { foreach (var e in Entities) if (e.Id == id) return e; return null; }
    public SkConstraint? Constraint(int id) { foreach (var c in Constraints) if (c.Id == id) return c; return null; }
    public Vec2 P(int id) => Point(id)?.P ?? Vec2.Zero;

    public SkPoint AddPoint(Vec2 p, bool fix = false)
    {
        var pt = new SkPoint { Id = NewId(), X = p.X, Y = p.Y, Fixed = fix };
        Points.Add(pt);
        return pt;
    }

    public T Add<T>(T e) where T : SkEntity { e.Id = NewId(); Entities.Add(e); return e; }

    public SkConstraint AddConstraint(CType type, params int[] refs)
    {
        var c = new SkConstraint { Id = NewId(), Type = type, Refs = refs };
        Constraints.Add(c);
        return c;
    }

    public double ArcRadius(SkArc a) => Vec2.Distance(P(a.Center), P(a.Start));
    public double Radius(SkEntity e) => e switch { SkCircle c => c.R, SkArc a => ArcRadius(a), _ => 0 };

    public Sketch Clone() => new()
    {
        Points = Points.Select(p => p.Clone()).ToList(),
        Entities = Entities.Select(e => e.Clone()).ToList(),
        Constraints = Constraints.Select(c => c.Clone()).ToList(),
        Fillets = Fillets?.Select(f => f.Clone()).ToList(),
        NextId = NextId,
    };

    /// <summary>Entities that reference a point.</summary>
    public IEnumerable<SkEntity> EntitiesUsing(int pointId) => Entities.Where(e => e.PointIds.Contains(pointId));

    /// <summary>Merges point 'from' into point 'to' (coincident relation).</summary>
    public void MergePoints(int from, int to)
    {
        if (from == to) return;
        var pf = Point(from); var pt = Point(to);
        if (pf == null || pt == null) return;
        if (pf.Fixed && !pt.Fixed) { (from, to) = (to, from); (pf, pt) = (pt, pf); }
        foreach (var e in Entities) e.RemapPoint(from, to);
        foreach (var c in Constraints)
            for (int i = 0; i < c.Refs.Length; i++) if (c.Refs[i] == from) c.Refs[i] = to;
        Points.Remove(pf);
        // drop constraints that became degenerate (e.g. distance between the same point)
        Constraints.RemoveAll(c => c.Refs.Length >= 2 && c.Refs.Distinct().Count() < c.Refs.Length &&
            c.Type is CType.Distance or CType.HDistance or CType.VDistance or CType.Coincident or CType.Horizontal or CType.Vertical);
    }

    /// <summary>
    /// Deletes an entity, its now-unused points, and constraints referencing them. A fillet's arc takes the fillet
    /// with it: its trimmed lines go and the original lines are solid again (the corner as before). Deleting
    /// another piece of a fillet leaves the rest as plain geometry.
    /// </summary>
    public void DeleteEntity(int id)
    {
        if (Fillets?.FirstOrDefault(f => f.Arc == id) is { } fillet) { RemoveFillet(fillet); return; }
        // another piece of a fillet: the fillets using it become plain geometry
        if (Fillets != null)
        {
            Fillets.RemoveAll(f => f.TrimA == id || f.TrimB == id || f.LineA == id || f.LineB == id);
            if (Fillets.Count == 0) Fillets = null;
        }
        DeleteEntityOnly(id);
    }

    /// <summary>
    /// Removes a fillet, restoring its corner: each trimmed line ends at the corner again, and when no other
    /// fillet trims it, it goes and the original line is solid again.
    /// </summary>
    void RemoveFillet(SkFillet f)
    {
        Fillets!.Remove(f);
        if (Fillets.Count == 0) Fillets = null;
        if (Entity(f.Arc) is SkArc arc && Entity(f.LineA) is SkLine la && Entity(f.LineB) is SkLine lb)
        {
            var corner = la.P1 == lb.P1 || la.P1 == lb.P2 ? la.P1 : la.P2 == lb.P1 || la.P2 == lb.P2 ? la.P2 : -1;
            foreach (var (orig, trimId) in new[] { (la, f.TrimA), (lb, f.TrimB) })
            {
                if (corner < 0 || Entity(trimId) is not SkLine trim) continue;
                // the trimmed line's end at this fillet's tangent point goes back to the corner
                if (trim.P1 == arc.Start || trim.P1 == arc.End) trim.P1 = corner;
                else if (trim.P2 == arc.Start || trim.P2 == arc.End) trim.P2 = corner;
                // spanning the whole original again (no other fillet on it): the original is the line again
                var whole = (trim.P1 == orig.P1 && trim.P2 == orig.P2) || (trim.P1 == orig.P2 && trim.P2 == orig.P1);
                if (whole && Fillets?.Any(o => o.TrimA == trimId || o.TrimB == trimId) != true)
                {
                    DeleteEntityOnly(trimId);
                    orig.Construction = false;
                }
            }
        }
        DeleteEntityOnly(f.Arc); // with its center and tangent points, and their relations
    }

    void DeleteEntityOnly(int id)
    {
        var e = Entity(id);
        if (e == null) return;
        Entities.Remove(e);
        var removed = new HashSet<int> { id };
        foreach (var pid in e.PointIds)
        {
            if (pid == OriginId) continue;
            if (!Entities.Any(o => o.PointIds.Contains(pid))) { Points.RemoveAll(p => p.Id == pid); removed.Add(pid); }
        }
        Constraints.RemoveAll(c => c.Refs.Any(removed.Contains));
    }

    public void DeleteConstraint(int id) => Constraints.RemoveAll(c => c.Id == id);

    /// <summary>Removes model-reference entities no constraint uses any more.</summary>
    public void RemoveUnusedReferences()
    {
        foreach (var e in Entities.Where(e => e.IsReference).ToList())
        {
            var ids = new HashSet<int>(e.PointIds) { e.Id };
            if (Constraints.Any(c => c.Refs.Any(ids.Contains))) continue;
            // sketch geometry sharing a reference point (snapped to a model vertex or circle center) keeps it
            if (Entities.Any(o => !o.IsReference && o.PointIds.Any(ids.Contains))) continue;
            DeleteEntity(e.Id);
        }
    }

    /// <summary>Removes points not referenced by any entity (except the origin).</summary>
    public void Cleanup()
    {
        var used = new HashSet<int> { OriginId };
        foreach (var e in Entities) foreach (var p in e.PointIds) used.Add(p);
        var removed = Points.Where(p => !used.Contains(p.Id)).Select(p => p.Id).ToHashSet();
        Points.RemoveAll(p => removed.Contains(p.Id));
        Constraints.RemoveAll(c => c.Refs.Any(removed.Contains));
    }
}
