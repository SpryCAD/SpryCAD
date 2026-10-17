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
    /// <summary>"H" or "V": the sketch's own horizontal or vertical axis through its origin (no model edge).</summary>
    public string? Axis { get; set; }
    /// <summary>
    /// Another sketch (earlier in the tree) whose geometry this is, in place of a model edge: its point
    /// <see cref="SketchItem"/> when <see cref="Vertex"/>, else its line or circle <see cref="SketchItem"/>.
    /// </summary>
    public string? Sketch { get; set; }
    public long SketchItem { get; set; }

    public bool SameAs(SkExternal o) =>
        Sketch != null || o.Sketch != null
            ? Sketch == o.Sketch && SketchItem == o.SketchItem && Vertex == o.Vertex
            : Axis == o.Axis && Vertex == o.Vertex && Vec3.Distance(Hint, o.Hint) < 1e-6 &&
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
[JsonDerivedType(typeof(SkSpline), "spline")]
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

/// <summary>
/// A spline through its points, in order (cubic; closed: round through the first point again, smooth there too). The
/// points are ordinary sketch points: relations and dimensions hold them, and the curve follows.
/// </summary>
public sealed class SkSpline : SkEntity
{
    public List<long> Points { get; set; } = new();
    public bool Closed { get; set; }
    /// <summary>
    /// Handles: its derivative leaving its first point, arriving at its last (sketch x–y, per its parameter 0 to 1:
    /// the direction and how far it runs on that way; null: free), where no Tangent relation sets the direction (then
    /// its length stays). Trim keeps a piece's shape with them.
    /// </summary>
    public Vec2? StartDir { get; set; }
    public Vec2? EndDir { get; set; }
    /// <summary>The points' parameters (0 to 1, rising: one each), kept from a curve it was cut from; null: centripetal.</summary>
    public List<double>? Params { get; set; }
    public override IEnumerable<long> PointIds => Points;
    public override SkEntity Clone() { var c = (SkSpline)MemberwiseClone(); c.Points = new List<long>(Points); c.Params = Params == null ? null : new List<double>(Params); return c; }
    public override void RemapPoint(long from, long to) { for (int i = 0; i < Points.Count; i++) if (Points[i] == from) Points[i] = to; }
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
    Length,                         // spline: its length along the curve
    Curvature,                      // spline + line/arc/spline at its end: tangent there, and as curved (G2)
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
    /// <summary>
    /// A distance to a centerline placed across it (on its far side from the geometry): a diameter, the value twice
    /// the distance, as a revolved part is dimensioned. Placed between them, it is the distance itself.
    /// </summary>
    public bool Doubled { get; set; }
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

    [JsonIgnore] public bool IsDimension => Type is CType.Distance or CType.HDistance or CType.VDistance or CType.Radius or CType.Diameter or CType.Angle or CType.Offset or CType.Length;
    /// <summary>What a dimension's value is: an angle (degrees) or a length (millimetres).</summary>
    [JsonIgnore] public Model.ValueKind ValueKind => Type == CType.Angle ? Model.ValueKind.Angle : Model.ValueKind.Length;
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

public enum PictureEdges { None, Over, Only }

/// <summary>A sketch picture's processing: the background removed, its edges found (<see cref="PictureFilter"/>).</summary>
public sealed record PictureLook(string? Background, double Tolerance, bool Connected, PictureEdges Edges, double Sensitivity)
{
    public const double DefaultTolerance = 0.15, DefaultSensitivity = 0.5;
    /// <summary>The image as it is: nothing to process.</summary>
    public bool Plain => Background == null && Edges == PictureEdges.None;
}

/// <summary>
/// An image placed on the sketch plane to trace over (Sketch Picture). Its bytes are an embedded file of the part
/// (<see cref="Model.CadDocument.Files"/>), named by <see cref="File"/>; the sketch only places it.
/// </summary>
public sealed class SkPicture
{
    public long Id { get; set; }
    /// <summary>The embedded file holding the image ("pictures/….png").</summary>
    public string File { get; set; } = "";
    /// <summary>The image file's own name, as it was inserted ("drawing.png").</summary>
    public string Name { get; set; } = "";
    /// <summary>The image's size in pixels (its aspect ratio, without reading the image).</summary>
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    /// <summary>The center of the picture in sketch coordinates.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Its size along its own axes, in model units (millimeters).</summary>
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>Counterclockwise from the sketch's X axis, in radians, about its center.</summary>
    public double Angle { get; set; }
    /// <summary>0 (invisible) to 1 (opaque).</summary>
    public double Opacity { get; set; } = 1;
    public bool Hidden { get; set; }
    /// <summary>The background color removed ("#RRGGBB"; null: none removed).</summary>
    public string? Background { get; set; }
    /// <summary>How far a color may be from <see cref="Background"/> and still be removed, 0 to 1 (null: 0.15).</summary>
    public double? BackgroundTolerance { get; set; }
    /// <summary>Only background reached from the picture's border without crossing other colors is removed (null: false).</summary>
    public bool? BackgroundConnected { get; set; }
    /// <summary>Edges found in the image, drawn over it or alone (null: none).</summary>
    public PictureEdges? Edges { get; set; }
    /// <summary>How many edges are found, 0 (only the strongest) to 1 (fainter ones too) (null: 0.5).</summary>
    public double? EdgeSensitivity { get; set; }

    /// <summary>How the image is processed for display (equal values: the same image).</summary>
    [JsonIgnore] public PictureLook Look => new(Background, BackgroundTolerance ?? PictureLook.DefaultTolerance, BackgroundConnected ?? false,
        Edges ?? PictureEdges.None, EdgeSensitivity ?? PictureLook.DefaultSensitivity);

    [JsonIgnore] public Vec2 Center { get => new(X, Y); set { X = value.X; Y = value.Y; } }
    /// <summary>The picture's right (X) and up (Y) directions in the sketch, unit length.</summary>
    [JsonIgnore] public Vec2 AxisX => new(Math.Cos(Angle), Math.Sin(Angle));
    [JsonIgnore] public Vec2 AxisY => new(-Math.Sin(Angle), Math.Cos(Angle));

    /// <summary>A sketch point in the picture's own coordinates (from its center, along its axes).</summary>
    public Vec2 ToLocal(Vec2 p) { var d = p - Center; return new(Vec2.Dot(d, AxisX), Vec2.Dot(d, AxisY)); }
    public Vec2 FromLocal(Vec2 q) => Center + AxisX * q.X + AxisY * q.Y;

    /// <summary>The corners: lower left, lower right, upper right, upper left.</summary>
    public Vec2[] Corners()
    {
        double w = Width / 2, h = Height / 2;
        return new[] { FromLocal(new(-w, -h)), FromLocal(new(w, -h)), FromLocal(new(w, h)), FromLocal(new(-w, h)) };
    }

    public bool Contains(Vec2 p)
    {
        var q = ToLocal(p);
        return Math.Abs(q.X) <= Width / 2 && Math.Abs(q.Y) <= Height / 2;
    }

    public SkPicture Clone() => (SkPicture)MemberwiseClone();
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
    /// <summary>Pictures to sketch over (null when none). Not geometry: the solver and profiles never see them.</summary>
    public List<SkPicture>? Pictures { get; set; }
    public SkPicture? Picture(long id) => Pictures?.Find(p => p.Id == id);
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

    /// <summary>
    /// A spline's curve through its points now (in the sketch's x–y, z = 0; parameters 0 to 1); null when its points do
    /// not make one. An end tangent to a line or arc it meets there (a Tangent relation) leaves in that one's direction.
    /// </summary>
    /// <param name="at">Where the points are (the solver's trial positions), else where they are now.</param>
    public BSplineCurve? SplineCurve(SkSpline s, Func<long, Vec2>? at = null)
    {
        Vec2 P(long id) => at?.Invoke(id) ?? this.P(id);
        Vec3? start = null, end = null, startK = null, endK = null;
        bool exact = false;
        // (a spline whose direction comes, in a round, from itself: free while it is being worked out)
        var busy = _splinesBusy ??= new HashSet<SkSpline>(ReferenceEqualityComparer.Instance);
        // an offset copy: the original curve's true offset, sampled densely (its points' parameters and four between
        // each), ending on the copy's own end points, its ends' derivatives the offset curve's (the original's, times
        // 1 − d κ): it follows the offset between its points too
        if (OffsetSource(s) is { } src && busy.Add(s))
        {
            try
            {
                if (SplineCurve(src.orig, at) is { } oc)
                {
                    var prm = SplineFitParams(src.orig, at);
                    // the original's part the copy spans: from the foot of its start to the foot of its end (trimmed, at an
                    // inside corner; run on straight past the original's end, at an outside one)
                    double ta = 0, tb = 1;
                    if (!s.Closed) { ta = ExtendedFoot(oc, P(s.Points[0])).t; tb = ExtendedFoot(oc, P(s.Points[^1])).t; }
                    if (!s.Closed && !(tb > ta + 1e-9)) { ta = 0; tb = 1; }
                    var ends = s.Closed ? prm.Append(1.0).ToList() : new[] { ta }.Concat(prm.Where(x => x > ta + 1e-9 && x < tb - 1e-9)).Append(tb).ToList();
                    var ts = new List<double>();
                    for (int i = 0; i + 1 < ends.Count; i++)
                        for (int k = 0; k < 5; k++) ts.Add(ends[i] + (ends[i + 1] - ends[i]) * k / 5);
                    if (!s.Closed) ts.Add(tb);
                    // the original (run on straight past its ends) and its derivatives there
                    (Vec3 p, Vec3 d1, Vec3 d2) E(double t)
                    {
                        var e = Math.Clamp(t, 0, 1);
                        oc.Derivs(e, out var p0, out var d1, out var d2);
                        return t == e ? (p0, d1, d2) : (p0 + d1 * (t - e), d1, default);
                    }
                    Vec3 Off(double t)
                    {
                        var (p0, d1, _) = E(t);
                        var n = new Vec3(-d1.Y, d1.X, 0); n = n.Length > 0 ? n.Normalized : n;
                        return p0 + n * src.d;
                    }
                    // (per the copy's own parameter, 0 to 1 over ta to tb)
                    Vec3 D(double t)
                    {
                        var (_, d1, d2) = E(t);
                        var l = d1.Length;
                        var k = l > 1e-12 ? (d1.X * d2.Y - d1.Y * d2.X) / (l * l * l) : 0;
                        return d1 * ((1 - src.d * k) * (tb - ta));
                    }
                    var pts = ts.Select(Off).ToList();
                    if (!s.Closed)
                    {
                        // (on the copy's end points as solved: the neighbours' copies end there)
                        var e0 = P(s.Points[0]); var e1 = P(s.Points[^1]);
                        pts[0] = new Vec3(e0.X, e0.Y, 0); pts[^1] = new Vec3(e1.X, e1.Y, 0);
                    }
                    return SplineFit.Through(pts, s.Closed, s.Closed ? null : D(ta), s.Closed ? null : D(tb), exactEnds: true,
                        parameters: s.Closed ? null : ts.Select(t => (t - ta) / (tb - ta)).ToList());
                }
            }
            finally { busy.Remove(s); }
        }
        if (!s.Closed && s.Points.Count >= 2 && busy.Add(s))
        {
            try
            {
                foreach (var c in Constraints)
                    if (SplineTangent(c, at) is { } t && ReferenceEquals(t.spline, s))
                    {
                        // curvature continuous: as curved as what it meets, there (the curvature vector, as seen going on)
                        if (c.Type == CType.Curvature)
                        {
                            var kv = CurvatureAt(t.other, t.point, at);
                            if (t.atStart) startK = kv; else endK = kv;
                        }
                        // (as long as the handle there, if it has one, else as the points' chords: the fit's own length)
                        var hl = (t.atStart ? s.StartDir : s.EndDir)?.Length;
                        double chords = 0;
                        for (int k = 1; k < s.Points.Count; k++) chords += Vec2.Distance(P(s.Points[k]), P(s.Points[k - 1]));
                        var d = new Vec3(t.away.X, t.away.Y, 0) * (hl ?? chords);
                        if (t.atStart) start = d; else end = -d;
                    }
            }
            finally { busy.Remove(s); }
            start ??= s.StartDir is { } sd ? new Vec3(sd.X, sd.Y, 0) : null;
            end ??= s.EndDir is { } ed ? new Vec3(ed.X, ed.Y, 0) : null;
            exact = s.StartDir != null || s.EndDir != null;
        }
        // (with handles the ends' derivatives are given whole; a free end there keeps the fit's own)
        return SplineFit.Through(s.Points.Select(id => { var p = P(id); return new Vec3(p.X, p.Y, 0); }).ToList(), s.Closed, start, end, exact,
            s.Params != null && s.Params.Count == s.Points.Count ? s.Params : null, startK, endK);
    }

    [ThreadStatic] static HashSet<SkSpline>? _splinesBusy;

    /// <summary>
    /// An entity's curvature vector at one of its end points (towards its centre of curvature, 1 / radius long): none
    /// for a line, towards an arc's centre, a spline's own at its end.
    /// </summary>
    Vec3 CurvatureAt(SkEntity e, long point, Func<long, Vec2>? at)
    {
        Vec2 P(long id) => at?.Invoke(id) ?? this.P(id);
        switch (e)
        {
            case SkArc a:
                {
                    var r = P(a.Center) - P(point); var l2 = r.LengthSq;
                    return l2 > 0 ? new Vec3(r.X / l2, r.Y / l2, 0) : default;
                }
            case SkSpline os when SplineCurve(os, at) is { } oc:
                {
                    oc.Derivs(point == os.Points[0] ? 0 : 1, out _, out var d1, out var d2);
                    var l2 = d1.LengthSq;
                    if (l2 <= 0) return default;
                    var tn = d1 * (1 / Math.Sqrt(l2));
                    return (d2 - tn * Vec3.Dot(d2, tn)) * (1 / l2);
                }
        }
        return default;
    }

    /// <summary>
    /// The point of a curve (over 0 to 1, run on straight along its end tangents past its ends) nearest q: its
    /// parameter (below 0 or past 1 on the run-ons), the point and the derivative there.
    /// </summary>
    public static (double t, Vec2 p, Vec2 d) ExtendedFoot(BSplineCurve c, Vec2 q)
    {
        var q3 = new Vec3(q.X, q.Y, 0);
        // seeded from the nearest of a few samples
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i <= 64; i++) { var d = Vec3.Distance(c.Eval(i / 64.0), q3); if (d < bd) { bd = d; best = i; } }
        var t = c.ClosestParam(q3, 0, 1, best / 64.0);
        if (double.IsNaN(t)) t = best / 64.0;
        foreach (var end in new[] { 0.0, 1.0 })
        {
            if (Math.Abs(t - end) > 1e-9) continue;
            c.Derivs(end, out var pe, out var de, out _);
            var l2 = de.LengthSq;
            if (l2 <= 0) continue;
            var s = Vec3.Dot(q3 - pe, de) / l2;
            if (end == 0 && s < 0 || end == 1 && s > 0) { var pp = pe + de * s; return (end + s, new Vec2(pp.X, pp.Y), new Vec2(de.X, de.Y)); }
        }
        c.Derivs(t, out var p0, out var d0, out _);
        return (t, new Vec2(p0.X, p0.Y), new Vec2(d0.X, d0.Y));
    }

    /// <summary>
    /// The spline an offset copy is of, and the signed distance (to the left of the original's direction), from the
    /// Offset relation; null for a spline that is no copy.
    /// </summary>
    public (SkSpline orig, double d)? OffsetSource(SkSpline s)
    {
        foreach (var c in Constraints)
        {
            if (c.Type != CType.Offset) continue;
            for (int k = 0; k + 1 < c.Refs.Length; k += 2)
                if (c.Refs[k + 1] == s.Id && Entity(c.Refs[k]) is SkSpline o && o.Points.Count == s.Points.Count)
                    return (o, (c.Sides != null && k / 2 < c.Sides.Length ? c.Sides[k / 2] : 1) * c.Value);
        }
        return null;
    }

    /// <summary>
    /// The parameters a spline's fit gives its points (as <see cref="SplineFit.Through"/> does: kept ones, else
    /// centripetal; closed: round the loop, the first at 0).
    /// </summary>
    public double[] SplineFitParams(SkSpline s, Func<long, Vec2>? at = null)
    {
        Vec2 P(long id) => at?.Invoke(id) ?? this.P(id);
        int n = s.Points.Count;
        if (!s.Closed && s.Params is { } kp && kp.Count == n) return kp.ToArray();
        var t = new double[n];
        double total = 0;
        for (int i = 1; i < n; i++) { total += Math.Sqrt(Vec2.Distance(P(s.Points[i]), P(s.Points[i - 1]))); t[i] = total; }
        if (s.Closed) total += Math.Sqrt(Vec2.Distance(P(s.Points[0]), P(s.Points[^1])));
        if (total > 0) for (int i = 0; i < n; i++) t[i] /= total;
        if (!s.Closed && n > 1) t[^1] = 1;
        return t;
    }

    /// <summary>A curve's length (Gauss-Legendre, eight points a knot span).</summary>
    public static double CurveLength(BSplineCurve c)
    {
        double[] gx = { -0.9602898564975363, -0.7966664774136267, -0.5255324099163290, -0.1834346424956498, 0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363 };
        double[] gw = { 0.1012285362903763, 0.2223810344533745, 0.3137066458778873, 0.3626837833783620, 0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763 };
        var ks = c.Knots.Distinct().ToList();
        double len = 0;
        for (int i = 0; i + 1 < ks.Count; i++)
        {
            double a = ks[i], b = ks[i + 1], h = 0.5 * (b - a), m = 0.5 * (a + b);
            if (h <= 0) continue;
            for (int k = 0; k < 8; k++) { c.Derivs(m + h * gx[k], out _, out var d, out _); len += gw[k] * h * d.Length; }
        }
        return len;
    }

    /// <summary>
    /// A Tangent relation between a spline and a line, arc or spline meeting one of its ends: the spline, the point,
    /// whether it is the spline's start, and the direction away from the other entity there (along it). Of two
    /// splines, the second follows the first (its end takes the first's direction). Null for any other relation.
    /// </summary>
    public (SkSpline spline, SkEntity other, long point, bool atStart, Vec2 away)? SplineTangent(SkConstraint c, Func<long, Vec2>? at = null)
    {
        Vec2 P(long id) => at?.Invoke(id) ?? this.P(id);
        if (c.Type is not (CType.Tangent or CType.Curvature) || c.Refs.Length != 2) return null;
        var e0 = Entity(c.Refs[0]); var e1 = Entity(c.Refs[1]);
        var (sp, o) = e1 is SkSpline s1 ? (s1, e0) : e0 is SkSpline s0 ? (s0, e1) : (null, null);
        if (sp == null || o == null || sp.Closed || sp.Points.Count < 2) return null;
        foreach (var (pt, atStart) in new[] { (sp.Points[0], true), (sp.Points[^1], false) })
        {
            if (!o.PointIds.Contains(pt)) continue;
            Vec2 away;
            switch (o)
            {
                case SkLine l:
                    {
                        var other = l.P1 == pt ? l.P2 : l.P1;
                        away = P(pt) - P(other);
                        break;
                    }
                case SkArc a when pt == a.Start || pt == a.End:
                    {
                        // the arc runs counter-clockwise from its start: it leaves its start along t, arrives at its end along t
                        var r = P(pt) - P(a.Center); var t = new Vec2(-r.Y, r.X);
                        away = pt == a.Start ? -t : t;
                        break;
                    }
                case SkSpline os when !os.Closed && os.Points.Count >= 2 && (pt == os.Points[0] || pt == os.Points[^1]):
                    {
                        // the other spline's own direction at its end there (away from it: back along it at its start)
                        if (SplineCurve(os, at) is not { } oc) continue;
                        bool atOtherStart = pt == os.Points[0] && !(pt == os.Points[^1]);
                        oc.Derivs(atOtherStart ? 0 : 1, out _, out var od, out _);
                        away = atOtherStart ? new Vec2(-od.X, -od.Y) : new Vec2(od.X, od.Y);
                        break;
                    }
                default: continue;
            }
            if (away.Length < 1e-12) return null;
            return (sp, o, pt, atStart, away.Normalized);
        }
        return null;
    }
    public double Radius(SkEntity e) => e switch { SkCircle c => c.R, SkArc a => ArcRadius(a), _ => 0 };

    public Sketch Clone() => new()
    {
        Points = Points.Select(p => p.Clone()).ToList(),
        Entities = Entities.Select(e => e.Clone()).ToList(),
        Constraints = Constraints.Select(c => c.Clone()).ToList(),
        Fillets = Fillets?.Select(f => f.Clone()).ToList(),
        ConvertGroups = ConvertGroups?.Select(g => g.Clone()).ToList(),
        Pictures = Pictures?.Select(p => p.Clone()).ToList(),
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
        // (lines, or arcs: a fillet where an arc meets a line or arc keeps them so too)
        static long[] Ends(SkEntity? e) => e switch { SkLine l => new[] { l.P1, l.P2 }, SkArc a => new[] { a.Start, a.End }, _ => Array.Empty<long>() };
        if (Entity(f.Arc) is SkArc arc && Entity(f.LineA) is { } la && Entity(f.LineB) is { } lb)
        {
            var corner = Ends(la).Intersect(Ends(lb)).DefaultIfEmpty(-1).First();
            foreach (var (orig, trimId) in new[] { (la, f.TrimA), (lb, f.TrimB) })
            {
                var trim = Entity(trimId);
                if (corner < 0 || trim == null) continue;
                // the trimmed piece's end at this fillet's tangent point goes back to the corner
                switch (trim)
                {
                    case SkLine tl:
                        if (tl.P1 == arc.Start || tl.P1 == arc.End) tl.P1 = corner;
                        else if (tl.P2 == arc.Start || tl.P2 == arc.End) tl.P2 = corner;
                        break;
                    case SkArc ta:
                        if (ta.Start == arc.Start || ta.Start == arc.End) ta.Start = corner;
                        else if (ta.End == arc.Start || ta.End == arc.End) ta.End = corner;
                        break;
                }
                // spanning the whole original again (no other fillet on it): the original is solid again
                var whole = (trim, orig) switch
                {
                    (SkLine t1, SkLine o1) => (t1.P1 == o1.P1 && t1.P2 == o1.P2) || (t1.P1 == o1.P2 && t1.P2 == o1.P1),
                    (SkArc t2, SkArc o2) => t2.Start == o2.Start && t2.End == o2.End,
                    _ => false,
                };
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
