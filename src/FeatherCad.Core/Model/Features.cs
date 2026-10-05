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
[JsonDerivedType(typeof(FilletFeature), "fillet")]
[JsonDerivedType(typeof(ChamferFeature), "chamfer")]
[JsonDerivedType(typeof(HoleFeature), "hole")]
[JsonDerivedType(typeof(ShellFeature), "shell")]
[JsonDerivedType(typeof(LinearPatternFeature), "linearPattern")]
[JsonDerivedType(typeof(CircularPatternFeature), "circularPattern")]
[JsonDerivedType(typeof(MirrorFeature), "mirror")]
[JsonDerivedType(typeof(PlaneFeature), "plane")]
[JsonDerivedType(typeof(CombineFeature), "combine")]
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

public sealed class FilletFeature : Feature
{
    public override string Kind => "Fillet";
    public List<EdgeRef> Edges { get; set; } = new();
    public string Radius { get; set; } = "2";
    /// <summary>Also blend edges tangent-connected to the selected ones.</summary>
    public bool TangentPropagation { get; set; } = true;
}

public sealed class ChamferFeature : Feature
{
    public override string Kind => "Chamfer";
    public List<EdgeRef> Edges { get; set; } = new();
    public string Distance { get; set; } = "1";
    public bool TangentPropagation { get; set; } = true;
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
}

public sealed class MirrorFeature : Feature
{
    public override string Kind => "Mirror";
    public List<string> Seeds { get; set; } = new();
    public PlaneRef Plane { get; set; } = PlaneRef.Right;
}

public sealed class PlaneFeature : Feature
{
    public override string Kind => "Plane";
    public PlaneRef Base { get; set; } = PlaneRef.Front;
    public string Offset { get; set; } = "10";
    public bool Flip { get; set; }
}

public enum CombineOp { Add, Subtract, Common }

public sealed class CombineFeature : Feature
{
    public override string Kind => "Combine";
    public CombineOp Op { get; set; } = CombineOp.Add;
    public string MainBody { get; set; } = "";
    public List<string> ToolBodies { get; set; } = new();
}

public sealed class Equation
{
    public string Name { get; set; } = "";
    public string Expr { get; set; } = "0";
}
