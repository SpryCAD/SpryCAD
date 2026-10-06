using FeatherCad.Core.Model;
using FeatherCad.Kernel;

namespace FeatherCad.App.Session;

public enum SelKind { Face, Edge, Plane, Sketch, Feature, Body, Origin, Vertex, SketchSegment }

[Flags]
public enum SelFilter
{
    None = 0,
    Face = 1, Edge = 2, Plane = 4, Sketch = 8, Feature = 16, Body = 32,
    PlanarFace = 64,
    /// <summary>Model vertices and sketch points (only when a selection box asks for points).</summary>
    Vertex = 128,
    /// <summary>Individual sketch lines/arcs (only when a selection box asks for curves).</summary>
    SketchSegment = 256,
    Any = Face | Edge | Plane | Sketch | Feature,
}

/// <summary>A selected entity in the model.</summary>
public sealed class SelItem
{
    public SelKind Kind { get; init; }
    public string? BodyId { get; init; }
    /// <summary>Face tag name (Face) or plane name (Plane, e.g. "Front").</summary>
    public string? Name { get; init; }
    public Vec3 Point { get; init; }
    public Vec3 Normal { get; init; }
    public bool Planar { get; init; }
    public Plane? FacePlane { get; init; }
    public EdgeRef? Edge { get; init; }
    public int EdgeIndex { get; init; } = -1;
    public bool EdgeLinear { get; init; }
    public bool EdgeCircular { get; init; }
    /// <summary>Feature id for sketches, plane features and features.</summary>
    public string? FeatureId { get; init; }
    public Frame? Frame { get; init; }
    /// <summary>Sketch point id (Vertex picked in a sketch) or sketch entity id (SketchSegment).</summary>
    public int SketchItem { get; init; } = -1;

    public string Key => Kind switch
    {
        SelKind.Face => $"F:{BodyId}:{Name}",
        SelKind.Edge => $"E:{BodyId}:{EdgeIndex}:{Edge?.A}:{Edge?.B}",
        SelKind.Plane => $"P:{Name}:{FeatureId}",
        SelKind.Sketch => $"S:{FeatureId}",
        SelKind.Feature => $"X:{FeatureId}",
        SelKind.Body => $"B:{BodyId}",
        SelKind.Vertex => FeatureId != null ? $"V:{FeatureId}:{SketchItem}" : $"V:{BodyId}:{Point.X:0.######},{Point.Y:0.######},{Point.Z:0.######}",
        SelKind.SketchSegment => $"G:{FeatureId}:{SketchItem}",
        _ => "O",
    };

    public string Label(CadDocument doc) => Kind switch
    {
        SelKind.Face => $"Face<{ShortName(Name)}>",
        SelKind.Edge => $"Edge<{EdgeIndex + 1}>",
        SelKind.Plane => FeatureId != null ? doc.Find(FeatureId)?.Name ?? "Plane" : $"{Name} Plane",
        SelKind.Sketch => doc.Find(FeatureId!)?.Name ?? "Sketch",
        SelKind.Feature => doc.Find(FeatureId!)?.Name ?? "Feature",
        SelKind.Body => $"Body<{BodyId}>",
        SelKind.Vertex => FeatureId != null ? $"Point{SketchItem}@{doc.Find(FeatureId)?.Name}" : "Vertex<" + $"{Point.X:0.##}, {Point.Y:0.##}, {Point.Z:0.##}>",
        SelKind.SketchSegment => $"Line{SketchItem}@{doc.Find(FeatureId!)?.Name}",
        _ => "Origin",
    };

    static string ShortName(string? n) => n == null ? "" : n.Length > 24 ? n[..24] : n;

    public FaceRef ToFaceRef() => new() { Name = Name ?? "", Body = BodyId, Hint = Point, Normal = Normal };

    public PlaneRef? ToPlaneRef()
    {
        if (Kind == SelKind.Plane) return FeatureId != null ? new PlaneRef { Feature = FeatureId } : new PlaneRef { Std = Name };
        if (Kind == SelKind.Face && Planar) return new PlaneRef { Face = ToFaceRef() };
        if (Kind == SelKind.Sketch) return new PlaneRef { Feature = FeatureId };
        return null;
    }

    /// <summary>Vertex picks as a point reference (sketch point, or the end of an edge between named faces).</summary>
    public PointRef? ToPointRef()
    {
        if (Kind != SelKind.Vertex) return null;
        if (FeatureId != null) return new PointRef { Sketch = FeatureId, SketchPoint = SketchItem };
        if (Edge != null) { var e = Edge.Clone(); e.Hint = Point; return new PointRef { Vertex = e }; }
        return null;
    }

    /// <summary>Edges and sketch lines/arcs as a curve reference.</summary>
    public CurveRef? ToCurveRef()
    {
        if (Kind == SelKind.Edge && Edge != null) return new CurveRef { Edge = Edge.Clone() };
        if (Kind == SelKind.SketchSegment && FeatureId != null) return new CurveRef { Sketch = FeatureId, SketchEntity = SketchItem };
        return null;
    }

    public DirRef? ToDirRef()
    {
        if (Kind == SelKind.Edge && Edge != null) return new DirRef { Edge = Edge.Clone() };
        if (Kind == SelKind.Face) return new DirRef { Face = ToFaceRef() };
        if (Kind == SelKind.Plane) return new DirRef { Plane = ToPlaneRef() };
        return null;
    }

    /// <summary>The feature that created this face (face names start with the feature id).</summary>
    public string? OwningFeatureId
    {
        get
        {
            if (Kind == SelKind.Feature || Kind == SelKind.Sketch) return FeatureId;
            if (Kind == SelKind.Face && Name != null)
            {
                var dot = Name.IndexOf('.');
                return dot > 0 ? Name[..dot] : null;
            }
            return null;
        }
    }
}

/// <summary>Ordered selection set with change notification.</summary>
public sealed class SelectionSet
{
    readonly List<SelItem> _items = new();
    public IReadOnlyList<SelItem> Items => _items;
    public event Action? Changed;

    public bool Contains(SelItem s) => _items.Any(i => i.Key == s.Key);
    public void Clear() { if (_items.Count == 0) return; _items.Clear(); Changed?.Invoke(); }
    public void Set(SelItem s) { _items.Clear(); _items.Add(s); Changed?.Invoke(); }
    public void Add(SelItem s) { if (!Contains(s)) { _items.Add(s); Changed?.Invoke(); } }
    public void Remove(SelItem s) { if (_items.RemoveAll(i => i.Key == s.Key) > 0) Changed?.Invoke(); }
    public void Toggle(SelItem s) { if (Contains(s)) Remove(s); else Add(s); }
    public int Count => _items.Count;
    public SelItem? First => _items.Count > 0 ? _items[0] : null;
}
