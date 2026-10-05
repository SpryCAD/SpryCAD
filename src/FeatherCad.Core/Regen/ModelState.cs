using FeatherCad.Core.Sketching;
using FeatherCad.Kernel;

namespace FeatherCad.Core.Regen;

public sealed class Body
{
    public required string Id { get; init; }
    public required Solid Solid { get; init; }
    DisplayMesh? _mesh;
    public DisplayMesh Mesh => _mesh ??= DisplayMesh.Build(Solid);
    public bool HasMesh => _mesh != null;
}

/// <summary>Result of solving a sketch during a rebuild.</summary>
public sealed class SketchResult
{
    public required Frame Frame { get; init; }
    public required Sketch Sketch { get; init; }
    public required List<ProfileRegion> Regions { get; init; }
    public SolveResult? Solve { get; init; }
}

/// <summary>Tool body produced by a feature, kept so patterns/mirrors can re-apply it.</summary>
public sealed record ToolInfo(Solid Tool, bool IsCut, bool Merge);

/// <summary>Immutable model state after a feature.</summary>
public sealed class ModelState
{
    public List<Body> Bodies { get; init; } = new();
    public Dictionary<string, Frame> Planes { get; init; } = new();
    public Dictionary<string, ToolInfo> Tools { get; init; } = new();
    public Dictionary<string, SketchResult> Sketches { get; init; } = new();

    public static ModelState Initial() => new();

    public ModelState With(List<Body>? bodies = null) => new()
    {
        Bodies = bodies ?? Bodies,
        Planes = Planes,
        Tools = Tools,
        Sketches = Sketches,
    };

    public ModelState Copy() => new()
    {
        Bodies = new List<Body>(Bodies),
        Planes = new Dictionary<string, Frame>(Planes),
        Tools = new Dictionary<string, ToolInfo>(Tools),
        Sketches = new Dictionary<string, SketchResult>(Sketches),
    };

    public Box3 Bounds
    {
        get
        {
            var b = Box3.Empty;
            foreach (var body in Bodies) b.Add(body.Solid.Bounds);
            return b;
        }
    }

    public Body? FindBody(string id) => Bodies.FirstOrDefault(b => b.Id == id);
}

public sealed class FeatureStatus
{
    public string? Error { get; set; }
    public List<string> Warnings { get; } = new();
    public double Milliseconds { get; set; }
    /// <summary>Model edges (polylines) the feature failed on, for highlighting.</summary>
    public List<Vec3[]> ProblemEdges { get; } = new();
    public bool Ok => Error == null;
}

public sealed class RegenResult
{
    public required ModelState State { get; init; }
    public required Dictionary<string, FeatureStatus> Status { get; init; }
    public required List<ModelState> States { get; init; }
    public double Milliseconds { get; init; }
    public int Rebuilt { get; init; }
    public Dictionary<string, double> EquationValues { get; init; } = new();
    public Dictionary<string, string> EquationErrors { get; init; } = new();
}
