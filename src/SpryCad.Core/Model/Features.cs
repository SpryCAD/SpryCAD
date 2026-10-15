using System.Text.Json.Serialization;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Core.Model;

/// <summary>Reference to a face by its persistent name, plus a geometric hint for disambiguation.</summary>
public sealed class FaceRef
{
    public string Name { get; set; } = "";
    public string? Body { get; set; }
    public Vec3 Hint { get; set; }
    public Vec3 Normal { get; set; }
    public FaceRef Clone() => (FaceRef)MemberwiseClone();
}

/// <summary>Reference to a model edge: the two faces it separates, plus a point hint.</summary>
public sealed class EdgeRef
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public string? Body { get; set; }
    public Vec3 Hint { get; set; }
    public EdgeRef Clone() => (EdgeRef)MemberwiseClone();
}

/// <summary>Reference to a plane: a standard plane, a reference plane feature, or a planar face.</summary>
public sealed class PlaneRef
{
    /// <summary>"Front", "Top" or "Right".</summary>
    public string? Std { get; set; }
    /// <summary>Id of a PlaneFeature or SketchFeature (its sketch plane).</summary>
    public string? Feature { get; set; }
    public FaceRef? Face { get; set; }
    public PlaneRef Clone() => new() { Std = Std, Feature = Feature, Face = Face?.Clone() };
    public static PlaneRef Front => new() { Std = "Front" };
    public static PlaneRef Top => new() { Std = "Top" };
    public static PlaneRef Right => new() { Std = "Right" };
}

/// <summary>Reference to a direction or axis.</summary>
public sealed class DirRef
{
    /// <summary>"X", "Y" or "Z" (through the origin).</summary>
    public string? Axis { get; set; }
    public EdgeRef? Edge { get; set; }
    /// <summary>Planar face (normal) or cylindrical face (axis).</summary>
    public FaceRef? Face { get; set; }
    public PlaneRef? Plane { get; set; }
    /// <summary>A sketch line (from its first point to its second).</summary>
    public CurveRef? Curve { get; set; }
    /// <summary>Id of a reference axis (AxisFeature).</summary>
    public string? Feature { get; set; }
    public DirRef Clone() => new() { Axis = Axis, Edge = Edge?.Clone(), Face = Face?.Clone(), Plane = Plane?.Clone(), Curve = Curve?.Clone(), Feature = Feature };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SketchFeature), "sketch")]
[JsonDerivedType(typeof(ExtrudeFeature), "extrude")]
[JsonDerivedType(typeof(RevolveFeature), "revolve")]
[JsonDerivedType(typeof(SweepFeature), "sweep")]
[JsonDerivedType(typeof(HelicalSweepFeature), "helicalSweep")]
[JsonDerivedType(typeof(LoftFeature), "loft")]
[JsonDerivedType(typeof(ThreadFeature), "thread")]
[JsonDerivedType(typeof(FilletFeature), "fillet")]
[JsonDerivedType(typeof(ChamferFeature), "chamfer")]
[JsonDerivedType(typeof(HoleFeature), "hole")]
[JsonDerivedType(typeof(ShellFeature), "shell")]
[JsonDerivedType(typeof(DraftFeature), "draft")]
[JsonDerivedType(typeof(LinearPatternFeature), "linearPattern")]
[JsonDerivedType(typeof(CircularPatternFeature), "circularPattern")]
[JsonDerivedType(typeof(MirrorFeature), "mirror")]
[JsonDerivedType(typeof(PlaneFeature), "plane")]
[JsonDerivedType(typeof(AxisFeature), "axis")]
[JsonDerivedType(typeof(CombineFeature), "combine")]
[JsonDerivedType(typeof(ImportFeature), "import")]
[JsonDerivedType(typeof(FlatPatternFeature), "flatPattern")]
public abstract class Feature
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Suppressed { get; set; }
    /// <summary>
    /// A condition on the equations ("Teeth &lt; 30"): the feature is suppressed while it holds (is not 0), as if
    /// <see cref="Suppressed"/> were set. Null or blank: no condition. Features using a suppressed one are
    /// suppressed with it.
    /// </summary>
    public string? SuppressWhen { get; set; }
    [JsonIgnore] public abstract string Kind { get; }
    /// <summary>Feature ids this feature consumes (e.g. the sketch of an extrude). Used for tree nesting.</summary>
    [JsonIgnore] public virtual IEnumerable<string> Consumes => Array.Empty<string>();
    public Feature Clone() => (Feature)Serialization.CloneObject(this, GetType());
}

public sealed class SketchFeature : Feature
{
    public override string Kind => "Sketch";
    public PlaneRef Plane { get; set; } = PlaneRef.Front;
    public Sketch Sketch { get; set; } = new();
}

public enum EndCondition { Blind, ThroughAll, MidPlane, ThroughAllBoth, UpToSurface, UpToBody }

public sealed class ExtrudeFeature : Feature
{
    public override string Kind => IsCut ? "Cut-Extrude" : "Boss-Extrude";
    public string SketchId { get; set; } = "";
    public bool IsCut { get; set; }
    public EndCondition End { get; set; } = EndCondition.Blind;
    public string Depth { get; set; } = "10";
    public bool Reverse { get; set; }
    /// <summary>Up To Surface: the plane, or the planar or cylindrical face, the extrude ends at; Up To Body: a face of the body.</summary>
    public PlaneRef? UpTo { get; set; }
    /// <summary>Optional second direction depth (blank = none).</summary>
    public string? Depth2 { get; set; }
    public bool Merge { get; set; } = true;
    /// <summary>The side walls' draft angle (degrees; blank or 0: none): narrowing away from the sketch plane.</summary>
    public string? Draft { get; set; }
    /// <summary>The draft the other way: the walls widen away from the sketch plane.</summary>
    public bool DraftOutward { get; set; }
    public override IEnumerable<string> Consumes => new[] { SketchId };
}

public sealed class RevolveFeature : Feature
{
    public override string Kind => IsCut ? "Cut-Revolve" : "Revolve";
    public string SketchId { get; set; } = "";
    public bool IsCut { get; set; }
    /// <summary>Sketch line used as axis (0 = first centerline).</summary>
    public long AxisEntity { get; set; }
    /// <summary>
    /// An axis from the model instead (a cylindrical or conical face, a straight or circular edge, X / Y / Z): it must
    /// lie in the sketch's plane. Null: the sketch line <see cref="AxisEntity"/>.
    /// </summary>
    public DirRef? Axis { get; set; }
    public string Angle { get; set; } = "360";
    public bool Reverse { get; set; }
    public bool Merge { get; set; } = true;
    public override IEnumerable<string> Consumes => new[] { SketchId };
}

/// <summary>A closed profile swept along a path (a chain of lines and arcs, or a circle) from where the path meets the profile's plane.</summary>
public sealed class SweepFeature : Feature
{
    public override string Kind => IsCut ? "Cut-Sweep" : "Sweep";
    public string ProfileSketchId { get; set; } = "";
    public string PathSketchId { get; set; } = "";
    public bool IsCut { get; set; }
    public bool Merge { get; set; } = true;
    public override IEnumerable<string> Consumes => new[] { ProfileSketchId, PathSketchId };
}

/// <summary>How a loft leaves its first section or arrives at its last.</summary>
public enum LoftTangency { None, Normal }

/// <summary>
/// A solid lofted through profile sketches in order (closed profiles; the first or last may be a single point),
/// optionally following guide curves: sketches of open chains of lines and arcs through every profile.
/// </summary>
public sealed class LoftFeature : Feature
{
    public override string Kind => IsCut ? "Cut-Loft" : "Loft";
    /// <summary>The profiles in order: sketches; blank where the profile is a model face (<see cref="SectionFaces"/>).</summary>
    public List<string> SectionSketchIds { get; set; } = new();
    /// <summary>Planar model faces as profiles, at the places the sketch id is blank (null elsewhere; empty: none).</summary>
    public List<FaceRef?> SectionFaces { get; set; } = new();
    public List<string> GuideSketchIds { get; set; } = new();
    /// <summary>A sketch of one tangent chain of lines and arcs through every profile's plane: the loft follows it (blank: none).</summary>
    public string? CenterlineSketchId { get; set; }
    /// <summary>Runs on from the last profile back to the first (a ring; no end faces).</summary>
    public bool Closed { get; set; }
    /// <summary>Square to the first profile's plane (its tangent's length <see cref="StartLength"/> times the loft's length).</summary>
    public LoftTangency StartTangency { get; set; }
    public string StartLength { get; set; } = "1";
    public LoftTangency EndTangency { get; set; }
    public string EndLength { get; set; } = "1";
    public bool IsCut { get; set; }
    public bool Merge { get; set; } = true;
    public override IEnumerable<string> Consumes => SectionSketchIds.Where(id => id != "").Concat(GuideSketchIds).Concat(string.IsNullOrEmpty(CenterlineSketchId) ? Array.Empty<string>() : new[] { CenterlineSketchId });
}

/// <summary>
/// A closed profile swept along a helix about a centerline of its sketch (threads, springs, worms): turning while it
/// advances Pitch per turn along the centerline.
/// </summary>
public sealed class HelicalSweepFeature : Feature
{
    public override string Kind => IsCut ? "Cut-Helical" : "Helical";
    public string SketchId { get; set; } = "";
    /// <summary>Sketch line used as axis (0 = first centerline).</summary>
    public long AxisEntity { get; set; }
    /// <summary>An axis from the model instead of the sketch: a cylindrical face, a straight or circular edge, or X / Y / Z.</summary>
    public DirRef? Axis { get; set; }
    public string Pitch { get; set; } = "5";
    public string Revolutions { get; set; } = "3";
    public bool LeftHand { get; set; }
    /// <summary>Advance against the centerline's direction.</summary>
    public bool Reverse { get; set; }
    /// <summary>Run out at the end: straight on along the helix's tangent (a profile with arcs: the end's normal).</summary>
    public bool RunOut { get; set; }
    /// <summary>The run-out's length (blank: for a cut until the profile is clear of the model, for a boss one pitch).</summary>
    public string RunOutLength { get; set; } = "";
    public bool IsCut { get; set; }
    public bool Merge { get; set; } = true;
    public override IEnumerable<string> Consumes => new[] { SketchId };
}

/// <summary>Where a thread ends: a length from the start end, short of the face's far end, or out through it.</summary>
public enum ThreadEnd { Blind, UpToNext, ThroughAll }

/// <summary>
/// How a thread that stops in the material ends: abruptly, in a relief groove (an undercut for the threading tool,
/// DIN 76 style), or running out of the side along the helix's tangent (a shaft only).
/// </summary>
public enum ThreadRunout { None, Relief, Tangent }

/// <summary>
/// A modelled thread on a cylindrical face: external on a shaft, internal in a hole (ISO metric, 60° profile), from
/// the end nearest the picked point, with a fit clearance for printed parts.
/// </summary>
public sealed class ThreadFeature : Feature
{
    public override string Kind => "Thread";
    public FaceRef Face { get; set; } = new();
    /// <summary>"Auto" (the size fitting the face), a metric size such as "M10", or "Custom" (Pitch, and the face as major/minor diameter).</summary>
    public string Size { get; set; } = "Auto";
    /// <summary>Pitch for a custom thread.</summary>
    public string Pitch { get; set; } = "1.5";
    /// <summary>Blind (Length), up to the face's far end (stopping short of a shoulder), or through it (nuts, through holes).</summary>
    public ThreadEnd End { get; set; } = ThreadEnd.Blind;
    /// <summary>Thread length from the start end for a blind thread (blank: up to the face's far end).</summary>
    public string Length { get; set; } = "";
    /// <summary>A 45° lead-in chamfer (countersink in a hole) at the start end, and at the far end of a through thread.</summary>
    public bool Chamfer { get; set; } = true;
    /// <summary>
    /// Shown without modeling the helical groove (helix lines on the face; chamfers and relief kept): fast to rebuild
    /// and draw. Exports always model it. The View menu can show every thread modeled or cosmetic instead.
    /// </summary>
    public bool Cosmetic { get; set; }
    /// <summary>How the far end of a thread that stops in the material ends (not used by Through All).</summary>
    public ThreadRunout Runout { get; set; } = ThreadRunout.None;
    /// <summary>Extra room on the flanks and root (mm), for 3D printed threads.</summary>
    public string Clearance { get; set; } = "0";
    public bool LeftHand { get; set; }
    /// <summary>Start from the other end of the face.</summary>
    public bool Reverse { get; set; }
}

public sealed class FilletFeature : Feature
{
    public override string Kind => "Fillet";
    public List<EdgeRef> Edges { get; set; } = new();
    public string Radius { get; set; } = "2";
    /// <summary>Also blend edges tangent-connected to the selected ones.</summary>
    public bool TangentPropagation { get; set; } = true;
    /// <summary>A fillet running into neighbouring geometry is trimmed by it ("keep edge" overflow) instead of failing.</summary>
    public bool Overflow { get; set; } = true;
    /// <summary>
    /// Where the fillet wraps round another fillet and its edge runs on into one not picked (a boss's foot round its side
    /// fillet): how far back from that fillet's tangent point it fades out to nothing, ending there. Empty: over the
    /// whole wrap; "0": a square end.
    /// </summary>
    public string FadeLength { get; set; } = "";
    /// <summary>
    /// Also every edge of the body of the kind <see cref="ScopeKind"/> (a casting's blends in one feature); with
    /// <see cref="Features"/> as well, the edges between those features' faces only.
    /// </summary>
    public bool WholeBody { get; set; }
    /// <summary>
    /// Also the edges of the kind <see cref="ScopeKind"/> between these features' faces (their copies by patterns and
    /// mirrors included): one feature, between it and the rest of the body; several, between any two of them.
    /// </summary>
    public List<string> Features { get; set; } = new();
    /// <summary>Which edges <see cref="WholeBody"/> and <see cref="Features"/> take.</summary>
    public BlendScopeKind ScopeKind { get; set; } = BlendScopeKind.Concave;
}

/// <summary>The edges a fillet's scope takes: concave (material added), convex (rounded off) or both.</summary>
public enum BlendScopeKind { Concave, Convex, All }

/// <summary>Equal distances on both faces, a distance and an angle, or two distances (SolidWorks' chamfer types).</summary>
public enum ChamferType { Distance, AngleDistance, DistanceDistance }

public sealed class ChamferFeature : Feature
{
    public override string Kind => "Chamfer";
    public List<EdgeRef> Edges { get; set; } = new();
    public ChamferType Type { get; set; } = ChamferType.Distance;
    /// <summary>The equal distance, or the distance along the reference face.</summary>
    public string Distance { get; set; } = "1";
    /// <summary>Distance along the other face (<see cref="ChamferType.DistanceDistance"/>).</summary>
    public string Distance2 { get; set; } = "1";
    /// <summary>Angle of the chamfer to the reference face, degrees (<see cref="ChamferType.AngleDistance"/>).</summary>
    public string Angle { get; set; } = "45";
    /// <summary>Measure Distance and Angle on the edge's other face.</summary>
    public bool Flip { get; set; }
    public bool TangentPropagation { get; set; } = true;
    /// <summary>A chamfer running into neighbouring geometry is trimmed by it instead of failing.</summary>
    public bool Overflow { get; set; } = true;
    /// <summary>
    /// Where three of the chamfers meet at a corner, the point they leave cut off flat: a triangle through the
    /// corners of the faces between them (where each pair of chamfers parts).
    /// </summary>
    public bool FlatCorners { get; set; }
}

public enum HoleType { Simple, Counterbore, Countersink }

public sealed class HoleFeature : Feature
{
    public override string Kind => "Hole";
    /// <summary>Position sketch (points) on the face.</summary>
    public string SketchId { get; set; } = "";
    public HoleType Type { get; set; } = HoleType.Simple;
    public string Diameter { get; set; } = "6.6";
    public string Depth { get; set; } = "10";
    public bool ThroughAll { get; set; } = true;
    public string CbDiameter { get; set; } = "11";
    public string CbDepth { get; set; } = "6.5";
    public string CsDiameter { get; set; } = "13";
    public string CsAngle { get; set; } = "90";
    public bool DrillPoint { get; set; } = true;
    public override IEnumerable<string> Consumes => new[] { SketchId };
}

public sealed class ShellFeature : Feature
{
    public override string Kind => "Shell";
    public List<FaceRef> Faces { get; set; } = new();
    public string Thickness { get; set; } = "2";
}

/// <summary>
/// Draft (neutral plane): faces tapered by an angle to the pull direction, each about where it meets the neutral
/// plane, so the part narrows going along the pull direction (planar faces, and round faces along it).
/// </summary>
public sealed class DraftFeature : Feature
{
    public override string Kind => "Draft";
    /// <summary>The neutral plane: a planar face (the pull direction then goes into the part) or a plane.</summary>
    public PlaneRef Neutral { get; set; } = PlaneRef.Top;
    public List<FaceRef> Faces { get; set; } = new();
    /// <summary>The draft angle (degrees).</summary>
    public string Angle { get; set; } = "3";
    /// <summary>The pull direction the other way.</summary>
    public bool Reverse { get; set; }
}

public sealed class LinearPatternFeature : Feature
{
    public override string Kind => "LPattern";
    public List<string> Seeds { get; set; } = new();
    public DirRef Direction { get; set; } = new() { Axis = "X" };
    public string Spacing { get; set; } = "20";
    public string Count { get; set; } = "3";
    public bool Reverse { get; set; }
    public DirRef? Direction2 { get; set; }
    public string Spacing2 { get; set; } = "20";
    public string Count2 { get; set; } = "1";
    public bool Reverse2 { get; set; }
    /// <summary>Instances to skip, numbered from the seed (1) along direction 1, then row by row along direction 2.</summary>
    public List<int> Skip { get; set; } = new();
}

public sealed class CircularPatternFeature : Feature
{
    public override string Kind => "CirPattern";
    public List<string> Seeds { get; set; } = new();
    public DirRef Axis { get; set; } = new() { Axis = "Z" };
    public string Count { get; set; } = "4";
    public string Angle { get; set; } = "360";
    public bool EqualSpacing { get; set; } = true;
    public bool Reverse { get; set; }
    /// <summary>Instances to skip, numbered from the seed (1) around the axis.</summary>
    public List<int> Skip { get; set; } = new();
}

public sealed class MirrorFeature : Feature
{
    public override string Kind => "Mirror";
    public List<string> Seeds { get; set; } = new();
    public PlaneRef Plane { get; set; } = PlaneRef.Right;
}

/// <summary>How a reference plane is placed relative to its base plane.</summary>
public enum PlaneMode
{
    /// <summary>Parallel to the base plane at a distance.</summary>
    Offset,
    /// <summary>Parallel to the base plane through a point (model vertex or sketch point).</summary>
    ThroughPoint,
    /// <summary>The base plane rotated about an axis (edge or X/Y/Z) by an angle; optionally moved through a point.</summary>
    Angle,
    /// <summary>Normal to a curve (model edge or sketch line/arc) at a point on it, e.g. its end.</summary>
    NormalToCurve,
    /// <summary>Up to three references, each with a relation to the plane (as SolidWorks' plane): <see cref="PlaneFeature.Refs"/>.</summary>
    References,
}

/// <summary>How a plane relates to one of its references.</summary>
public enum PlaneRelation { Coincident, Parallel, Perpendicular, Offset, Angle, MidPlane, Tangent }

/// <summary>
/// One reference of a plane and its relation to it: a plane or flat face, a line (edge, sketch line or axis), a
/// round face, a curve or a point. A straight edge or sketch line is both a line and a curve: perpendicular to it is
/// at a point on it, its end nearest <see cref="Hint"/> when nothing else places the plane.
/// </summary>
public sealed class PlaneReference
{
    public PlaneRef? Plane { get; set; }
    /// <summary>A straight line, or the axis of a round face or circular edge.</summary>
    public DirRef? Line { get; set; }
    /// <summary>A round face (tangent to it; its axis is <see cref="Line"/>).</summary>
    public FaceRef? Round { get; set; }
    public CurveRef? Curve { get; set; }
    public PointRef? Point { get; set; }
    public PlaneRelation Relation { get; set; }
    /// <summary>The distance (Offset) or the angle in degrees (Angle).</summary>
    public string Value { get; set; } = "10";
    /// <summary>The other side: of the offset, of the angle, or of the round face (Tangent).</summary>
    public bool Flip { get; set; }
    /// <summary>Where it was picked: which end of a curve, which side of a round face.</summary>
    public Vec3 Hint { get; set; }
}

public sealed class PlaneFeature : Feature
{
    public override string Kind => "Plane";
    public PlaneMode Mode { get; set; } = PlaneMode.Offset;
    public PlaneRef Base { get; set; } = PlaneRef.Front;
    public string Offset { get; set; } = "10";
    /// <summary>Point the plane passes through (ThroughPoint; optional for Angle).</summary>
    public PointRef? Point { get; set; }
    /// <summary>Rotation axis (Angle).</summary>
    public DirRef? Axis { get; set; }
    /// <summary>Rotation angle in degrees (Angle).</summary>
    public string Angle { get; set; } = "45";
    /// <summary>The curve the plane is normal to (NormalToCurve); the plane passes through <see cref="Point"/>.</summary>
    public CurveRef? Curve { get; set; }
    public bool Flip { get; set; }
    /// <summary>The references and how the plane relates to each (References).</summary>
    public List<PlaneReference> Refs { get; set; } = new();
    /// <summary>The size it is drawn, as dragged by its handles (in its own X and Y); null: to fit what is sketched on it.</summary>
    public PlaneExtent? Size { get; set; }
}

/// <summary>The rectangle a plane is drawn as, in the plane's own coordinates (about its origin).</summary>
public sealed class PlaneExtent
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}

/// <summary>One reference of an axis: a plane or flat face, a line (edge, sketch line, round face's axis), or a point.</summary>
public sealed class AxisReference
{
    public PlaneRef? Plane { get; set; }
    public DirRef? Line { get; set; }
    public PointRef? Point { get; set; }
}

/// <summary>
/// A reference axis, as SolidWorks': along a line, edge or round face, where two planes meet, through two points, or
/// square to a plane through a point; which of them follows from what is picked.
/// </summary>
public sealed class AxisFeature : Feature
{
    public override string Kind => "Axis";
    public List<AxisReference> Refs { get; set; } = new();
}

/// <summary>A curve:a model edge (between named faces) or a line/arc of a sketch.</summary>
public sealed class CurveRef
{
    public EdgeRef? Edge { get; set; }
    public string? Sketch { get; set; }
    public long SketchEntity { get; set; }
    public CurveRef Clone() => new() { Edge = Edge?.Clone(), Sketch = Sketch, SketchEntity = SketchEntity };
}

/// <summary>
/// A point in the model: a model vertex (the end, nearest to <see cref="EdgeRef.Hint"/>, of the edge between the
/// named faces, so it follows model changes) or a point of a sketch (follows the sketch's dimensions).
/// </summary>
public sealed class PointRef
{
    public EdgeRef? Vertex { get; set; }
    /// <summary>The center of a circular edge (a circle or an arc of one): a hole's, a boss's.</summary>
    public EdgeRef? Center { get; set; }
    public string? Sketch { get; set; }
    public long SketchPoint { get; set; }
    public PointRef Clone() => new() { Vertex = Vertex?.Clone(), Center = Center?.Clone(), Sketch = Sketch, SketchPoint = SketchPoint };
}

public enum CombineOp { Add, Subtract, Common }

public sealed class CombineFeature : Feature
{
    public override string Kind => "Combine";
    public CombineOp Op { get; set; } = CombineOp.Add;
    public string MainBody { get; set; } = "";
    public List<string> ToolBodies { get; set; } = new();
}

/// <summary>
/// Solids read from a file embedded in the part (e.g. "imports/bracket.step"), one body per solid. Faces are
/// named after the file's own face ids, so features applied on top keep their references.
/// </summary>
public sealed class ImportFeature : Feature
{
    public override string Kind => "Imported";
    /// <summary>Name of the embedded file (<see cref="CadDocument.Files"/>).</summary>
    public string File { get; set; } = "";
    /// <summary>SHA-256 of the embedded file when it was imported (a replaced file rebuilds).</summary>
    public string Sha256 { get; set; } = "";
}

/// <summary>A part's sheet metal settings: how long its bends are in the flat.</summary>
public sealed class SheetMetalSettings
{
    public BendAllowanceMethod Method { get; set; } = BendAllowanceMethod.KFactor;
    /// <summary>Where the neutral fiber lies in a bend, as a fraction of the thickness from the inside (0..1).</summary>
    public string KFactor { get; set; } = "0.5";
    public SheetMetalSettings Clone() => (SheetMetalSettings)MemberwiseClone();
}

/// <summary>
/// The flat pattern of sheet metal bodies (any body of uniform thickness whose bends are cylinders), unfolded about
/// a fixed face. It is always computed (for DXF export); <see cref="Flatten"/> shows the part flat.
/// </summary>
public sealed class FlatPatternFeature : Feature
{
    public override string Kind => "Flat-Pattern";
    /// <summary>The planar face that stays in place; none: every body about its largest planar face.</summary>
    public FaceRef? FixedFace { get; set; }
    /// <summary>Overrides the part's bend allowance method (null: the part's, <see cref="CadDocument.SheetMetal"/>).</summary>
    public BendAllowanceMethod? Method { get; set; }
    /// <summary>Overrides the part's K-factor (null or blank: the part's).</summary>
    public string? KFactor { get; set; }
    public bool Flatten { get; set; }
}

public sealed class Equation
{
    public string Name { get; set; } = "";
    /// <summary>The value or expression; for a user-facing variable, the default a configuration may replace.</summary>
    public string Expr { get; set; } = "0";
    /// <summary>
    /// User-facing: shown in the Configure dialog, where an assembly sets it for each instance of the part (a
    /// screw's length). Otherwise internal: only the Equations dialog shows it.
    /// </summary>
    public bool Exposed { get; set; }
    /// <summary>What the variable is, for the Configure dialog ("Length under the head").</summary>
    public string? Description { get; set; }
    /// <summary>Smallest and largest value Configure accepts (null: no limit). 0 and 1 make it a checkbox.</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }
}
