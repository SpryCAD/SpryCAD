using System.Text.Json.Serialization;
using SpryCad.Kernel;

namespace SpryCad.Core.Sketching;

public sealed class SkPoint
{
    public long Id { get; set; }
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
    public SpryCad.Core.Model.EdgeRef Edge { get; set; } = new();
    /// <summary>True: the end vertex of <see cref="Edge"/> nearest to <see cref="Hint"/>.</summary>
    public bool Vertex { get; set; }
    public Vec3 Hint { get; set; }

    public bool SameAs(SkExternal o) =>
        Vertex == o.Vertex && Vec3.Distance(Hint, o.Hint) < 1e-6 &&
        ((Edge.A == o.Edge.A && Edge.B == o.Edge.B) || (Edge.A == o.Edge.B && Edge.B == o.Edge.A));
}

/// <summary>
/// Convert Entities' link ("On Edge", as SolidWorks'): the sketch entity is a piece of a model edge seen in the
/// sketch, placed again from the edge on every rebuild, so it follows the model as it changes. Unlike
/// <see cref="SkExternal"/> it is ordinary geometry (it makes profiles).
/// </summary>
public sealed class SkConvertLink
{
    public SpryCad.Core.Model.EdgeRef Edge { get; set; } = new();
    /// <summary>Which piece of the edge (a curve with no exact sketch equivalent is fitted with several), of how many.</summary>
    public int Piece { get; set; }
    public int Of { get; set; } = 1;
    /// <summary>The <see cref="SkConvertGroup"/> (a face or chain converted as a whole) it came from; null for an edge picked alone.</summary>
    public long? Group { get; set; }

    [JsonIgnore] public string Key => $"{Edge.Body}|{Edge.A}|{Edge.B}|{Edge.Hint.X:R},{Edge.Hint.Y:R},{Edge.Hint.Z:R}";
}

/// <summary>
/// A face (all its edges) or a tangent chain converted as a whole: when the model changes how many edges it has
/// (a gear given more teeth), its geometry is converted again from what it is now, not followed edge by edge.
/// </summary>
public sealed class SkConvertGroup
{
    public long Id { get; set; }
    public string Body { get; set; } = "";
    /// <summary>The face whose edges were converted (every face of the body by that name).</summary>
    public string? Face { get; set; }
    /// <summary>The edge a tangent chain was picked by.</summary>
    public SpryCad.Core.Model.EdgeRef? Chain { get; set; }
    /// <summary>How many edges it had when converted (fewer entities now: some were deleted, and stay so).</summary>
    public int Edges { get; set; }
    /// <summary>
    /// The edges converted (as last seen). An edge known here with no geometry on it had its geometry deleted, and
    /// it stays so; an edge not known here is new (more teeth, a hole added) and is converted.
    /// </summary>
    public List<SpryCad.Core.Model.EdgeRef>? Known { get; set; }
    public SkConvertGroup Clone()
    {
        var g = (SkConvertGroup)MemberwiseClone();
        g.Chain = Chain?.Clone();
        g.Known = Known?.Select(k => k.Clone()).ToList();
        return g;
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SkLine), "line")]
[JsonDerivedType(typeof(SkCircle), "circle")]
[JsonDerivedType(typeof(SkArc), "arc")]
[JsonDerivedType(typeof(SkPointEntity), "point")]
public abstract class SkEntity
{
    public long Id { get; set; }
    public bool Construction { get; set; }
    /// <summary>Reference to model geometry (fixed, not part of profiles).</summary>
    public SkExternal? External { get; set; }
    [JsonIgnore] public bool IsReference => External != null;
    /// <summary>Converted from a model edge and kept on it (Convert Entities); null for drawn geometry.</summary>
    public SkConvertLink? Link { get; set; }
    /// <summary>
    /// Converted geometry whose model face or edge is no longer in the model (its feature deleted, say): shown as
    /// dangling, not part of profiles. Found again on every rebuild, not saved.
    /// </summary>
    [JsonIgnore] public bool Dangling { get; set; }
    public abstract IEnumerable<long> PointIds { get; }
    public abstract SkEntity Clone();
    public abstract void RemapPoint(long from, long to);
}

public sealed class SkLine : SkEntity
{
    public long P1 { get; set; }
    public long P2 { get; set; }
    public override IEnumerable<long> PointIds { get { yield return P1; yield return P2; } }
    public override SkEntity Clone() => (SkEntity)MemberwiseClone();
    public override void RemapPoint(long from, long to) { if (P1 == from) P1 = to; if (P2 == from) P2 = to; }
}

public sealed class SkCircle : SkEntity
{
    public long Center { get; set; }
    public double R { get; set; }
    public override IEnumerable<long> PointIds { get { yield return Center; } }
    public override SkEntity Clone() => (SkEntity)MemberwiseClone();
    public override void RemapPoint(long from, long to) { if (Center == from) Center = to; }
}

/// <summary>Arc from Start to End counter-clockwise around Center.</summary>
public sealed class SkArc : SkEntity
{
    public long Center { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public override IEnumerable<long> PointIds { get { yield return Center; yield return Start; yield return End; } }
    public override SkEntity Clone() => (SkEntity)MemberwiseClone();
    public override void RemapPoint(long from, long to) { if (Center == from) Center = to; if (Start == from) Start = to; if (End == from) End = to; }
}

public sealed class SkPointEntity : SkEntity
{
    public long P { get; set; }
    public override IEnumerable<long> PointIds { get { yield return P; } }
    public override SkEntity Clone() => (SkEntity)MemberwiseClone();
    public override void RemapPoint(long from, long to) { if (P == from) P = to; }
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
    public long Id { get; set; }
    public CType Type { get; set; }
    /// <summary>Referenced point / entity ids (shared id space).</summary>
    public long[] Refs { get; set; } = Array.Empty<long>();
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
    public SkConstraint Clone() { var c = (SkConstraint)MemberwiseClone(); c.Refs = (long[])Refs.Clone(); c.Sides = (int[]?)Sides?.Clone(); return c; }
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
    public long Arc { get; set; }
    public long LineA { get; set; }
    public long LineB { get; set; }
    public long TrimA { get; set; }
    public long TrimB { get; set; }
    public SkFillet Clone() => (SkFillet)MemberwiseClone();
}

public sealed class Sketch
{
    public const long OriginId = 0;

    public List<SkPoint> Points { get; set; } = new() { new SkPoint { Id = OriginId, Fixed = true } };
    public List<SkEntity> Entities { get; set; } = new();
    public List<SkConstraint> Constraints { get; set; } = new();
    /// <summary>Sketch fillets (null when none, so sketches without them are saved as before).</summary>
    public List<SkFillet>? Fillets { get; set; }
    /// <summary>What Convert Entities was given (a face, a chain), converted again as it changes (null when none).</summary>
    public List<SkConvertGroup>? ConvertGroups { get; set; }
    /// <summary>A new id for a point, entity or relation (random: <see cref="Model.Ids"/>).</summary>
    public long NewId() => Model.Ids.NewSketchId();

    public SkPoint? Point(long id)
    {
        var ps = Points;
        if (ps.Count > IndexFrom)
        {
            var ix = _points;
            if (ix == null || !ReferenceEquals(ix.List, ps) || ix.Version != ListVersion(ps)) _points = ix = new(ps, ListVersion(ps), IndexOf(ps, p => p.Id));
            if (!ix.At.TryGetValue(id, out var k)) return null;
            if (ps[k].Id == id) return ps[k];
        }
        foreach (var p in ps) if (p.Id == id) return p;
        return null;
    }

    public SkEntity? Entity(long id)
    {
        var es = Entities;
        if (es.Count > IndexFrom)
        {
            var ix = _entities;
            if (ix == null || !ReferenceEquals(ix.List, es) || ix.Version != ListVersion(es)) _entities = ix = new(es, ListVersion(es), IndexOf(es, e => e.Id));
            if (!ix.At.TryGetValue(id, out var k)) return null;
            if (es[k].Id == id) return es[k];
        }
        foreach (var e in es) if (e.Id == id) return e;
        return null;
    }

    // Point and Entity by id, for sketches past a few dozen items (drawing and the solver look up every entity's
    // points and every relation's references): each id's first place in its list, valid while the list is the same
    // object at the same version (List<T> counts every change made to it; the lists are edited in place throughout).
    // Ids are given before an item joins its list (a hit is still checked, and falls back to the scan). Built whole
    // and swapped in, so threads reading one sketch never see it half made.
    sealed record Index<T>(List<T> List, int Version, Dictionary<long, int> At);
    Index<SkPoint>? _points;
    Index<SkEntity>? _entities;
    const int IndexFrom = 16;

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_version")]
    static extern ref int VersionOf(List<SkPoint> list);
    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_version")]
    static extern ref int VersionOf(List<SkEntity> list);
    static int ListVersion(List<SkPoint> list) => VersionOf(list);
    static int ListVersion(List<SkEntity> list) => VersionOf(list);

    static Dictionary<long, int> IndexOf<T>(List<T> list, Func<T, long> id)
    {
        var d = new Dictionary<long, int>(list.Count);
        for (int i = 0; i < list.Count; i++) d.TryAdd(id(list[i]), i);
        return d;
    }
    public SkConstraint? Constraint(long id) { foreach (var c in Constraints) if (c.Id == id) return c; return null; }
    public Vec2 P(long id) => Point(id)?.P ?? Vec2.Zero;

    public SkPoint AddPoint(Vec2 p, bool fix = false)
    {
        var pt = new SkPoint { Id = NewId(), X = p.X, Y = p.Y, Fixed = fix };
        Points.Add(pt);
        return pt;
    }

    public T Add<T>(T e) where T : SkEntity { e.Id = NewId(); Entities.Add(e); return e; }

    public SkConstraint AddConstraint(CType type, params long[] refs)
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
        ConvertGroups = ConvertGroups?.Select(g => g.Clone()).ToList(),
    };

    /// <summary>Entities that reference a point.</summary>
    public IEnumerable<SkEntity> EntitiesUsing(long pointId) => Entities.Where(e => e.PointIds.Contains(pointId));

    /// <summary>Merges point 'from' into point 'to' (coincident relation).</summary>
    public void MergePoints(long from, long to)
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
    public void DeleteEntity(long id)
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

    void DeleteEntityOnly(long id)
    {
        var e = Entity(id);
        if (e == null) return;
        Entities.Remove(e);
        var removed = new HashSet<long> { id };
        foreach (var pid in e.PointIds)
        {
            if (pid == OriginId) continue;
            if (!Entities.Any(o => o.PointIds.Contains(pid))) { Points.RemoveAll(p => p.Id == pid); removed.Add(pid); }
        }
        Constraints.RemoveAll(c => c.Refs.Any(removed.Contains));
        // the last of a converted face or chain deleted: the conversion goes (it is not made again)
        if (e.Link?.Group is { } g) DropGroupIfEmpty(g);
    }

    void DropGroupIfEmpty(long group)
    {
        if (ConvertGroups == null || Entities.Any(x => x.Link?.Group == group)) return;
        ConvertGroups.RemoveAll(x => x.Id == group);
        if (ConvertGroups.Count == 0) ConvertGroups = null;
    }

    public void DeleteConstraint(long id) => Constraints.RemoveAll(c => c.Id == id);

    /// <summary>
    /// Takes converted geometry off its model edge (SolidWorks: deleting On Edge): it stays where it is as ordinary,
    /// free geometry; points it shares with geometry still on an edge stay held there.
    /// </summary>
    public void Unlink(SkEntity e)
    {
        if (e.Link == null) return;
        var group = e.Link.Group;
        e.Link = null;
        if (group is { } g) DropGroupIfEmpty(g);
        foreach (var pid in e.PointIds)
            if (pid != OriginId && !Entities.Any(o => o.Link != null && o.PointIds.Contains(pid)) && Point(pid) is { } p) p.Fixed = false;
    }

    /// <summary>Removes model-reference entities no constraint uses any more.</summary>
    public void RemoveUnusedReferences()
    {
        foreach (var e in Entities.Where(e => e.IsReference).ToList())
        {
            var ids = new HashSet<long>(e.PointIds) { e.Id };
            if (Constraints.Any(c => c.Refs.Any(ids.Contains))) continue;
            // sketch geometry sharing a reference point (snapped to a model vertex or circle center) keeps it
            if (Entities.Any(o => !o.IsReference && o.PointIds.Any(ids.Contains))) continue;
            DeleteEntity(e.Id);
        }
    }

    /// <summary>Removes points not referenced by any entity (except the origin).</summary>
    public void Cleanup()
    {
        var used = new HashSet<long> { OriginId };
        foreach (var e in Entities) foreach (var p in e.PointIds) used.Add(p);
        var removed = Points.Where(p => !used.Contains(p.Id)).Select(p => p.Id).ToHashSet();
        Points.RemoveAll(p => removed.Contains(p.Id));
        Constraints.RemoveAll(c => c.Refs.Any(removed.Contains));
    }
}
