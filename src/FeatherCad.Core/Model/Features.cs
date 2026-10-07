using System.Text.Json.Serialization;
using FeatherCad.Core.Sketching;
using FeatherCad.Kernel;

namespace FeatherCad.Core.Model;

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
    public DirRef Clone() => new() { Axis = Axis, Edge = Edge?.Clone(), Face = Face?.Clone(), Plane = Plane?.Clone() };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SketchFeature), "sketch")]
[JsonDerivedType(typeof(ExtrudeFeature), "extrude")]
[JsonDerivedType(typeof(RevolveFeature), "revolve")]
[JsonDerivedType(typeof(SweepFeature), "sweep")]
[JsonDerivedType(typeof(HelicalSweepFeature), "helicalSweep")]
[JsonDerivedType(typeof(ThreadFeature), "thread")]
[JsonDerivedType(typeof(FilletFeature), "fillet")]
[JsonDerivedType(typeof(ChamferFeature), "chamfer")]
[JsonDerivedType(typeof(HoleFeature), "hole")]
[JsonDerivedType(typeof(ShellFeature), "shell")]
[JsonDerivedType(typeof(LinearPatternFeature), "linearPattern")]
[JsonDerivedType(typeof(CircularPatternFeature), "circularPattern")]
[JsonDerivedType(typeof(MirrorFeature), "mirror")]
[JsonDerivedType(typeof(PlaneFeature), "plane")]
[JsonDerivedType(typeof(CombineFeature), "combine")]
[JsonDerivedType(typeof(ImportFeature), "import")]
[JsonDerivedType(typeof(FlatPatternFeature), "flatPattern")]
public abstract class Feature
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Suppressed { get; set; }
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

public enum EndCondition { Blind, ThroughAll, MidPlane, ThroughAllBoth }

public sealed class ExtrudeFeature : Feature
{
    public override string Kind => IsCut ? "Cut-Extrude" : "Boss-Extrude";
    public string SketchId { get; set; } = "";
    public bool IsCut { get; set; }
    public EndCondition End { get; set; } = EndCondition.Blind;
    public string Depth { get; set; } = "10";
    public bool Reverse { get; set; }
    /// <summary>Optional second direction depth (blank = none).</summary>
    public string? Depth2 { get; set; }
    public bool Merge { get; set; } = true;
    public override IEnumerable<string> Consumes => new[] { SketchId };
}

public sealed class RevolveFeature : Feature
{
    public override string Kind => IsCut ? "Cut-Revolve" : "Revolve";
    public string SketchId { get; set; } = "";
    public bool IsCut { get; set; }
    /// <summary>Sketch line used as axis (0 = first centerline).</summary>
    public int AxisEntity { get; set; }
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

/// <summary>
/// A closed profile swept along a helix about a centerline of its sketch (threads, springs, worms): turning while it
/// advances Pitch per turn along the centerline.
/// </summary>
public sealed class HelicalSweepFeature : Feature
{
    public override string Kind => IsCut ? "Cut-Helical" : "Helical";
    public string SketchId { get; set; } = "";
    /// <summary>Sketch line used as axis (0 = first centerline).</summary>
    public int AxisEntity { get; set; }
    public string Pitch { get; set; } = "5";
    public string Revolutions { get; set; } = "3";
    public bool LeftHand { get; set; }
    /// <summary>Advance against the centerline's direction.</summary>
    public bool Reverse { get; set; }
    public bool IsCut { get; set; }
    public bool Merge { get; set; } = true;
    public override IEnumerable<string> Consumes => new[] { SketchId };
}

/// <summary>Where a thread ends: a length from the start end, short of the face's far end, or out through it.</summary>
public enum ThreadEnd { Blind, UpToNext, ThroughAll }

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
}

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
}

/// <summary>A curve: a model edge (between named faces) or a line/arc of a sketch.</summary>
public sealed class CurveRef
{
    public EdgeRef? Edge { get; set; }
    public string? Sketch { get; set; }
    public int SketchEntity { get; set; }
    public CurveRef Clone() => new() { Edge = Edge?.Clone(), Sketch = Sketch, SketchEntity = SketchEntity };
}

/// <summary>
/// A point in the model: a model vertex (the end, nearest to <see cref="EdgeRef.Hint"/>, of the edge between the
/// named faces, so it follows model changes) or a point of a sketch (follows the sketch's dimensions).
/// </summary>
public sealed class PointRef
{
    public EdgeRef? Vertex { get; set; }
    public string? Sketch { get; set; }
    public int SketchPoint { get; set; }
    public PointRef Clone() => new() { Vertex = Vertex?.Clone(), Sketch = Sketch, SketchPoint = SketchPoint };
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
    public string Expr { get; set; } = "0";
}
