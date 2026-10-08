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
/// <summary>The tool solid a feature applied (for patterns/mirrors); combined tools are built on first use.</summary>
public sealed class ToolInfo
{
    readonly Lazy<Solid> _tool;
    public ToolInfo(Solid tool, bool isCut, bool merge) : this(() => tool, isCut, merge) { }
    public ToolInfo(Func<Solid> tool, bool isCut, bool merge) { _tool = new Lazy<Solid>(tool); IsCut = isCut; Merge = merge; }
    public Solid Tool => _tool.Value;
    public bool IsCut { get; }
    public bool Merge { get; }
    /// <summary>For fillets/chamfers: how to solve the blend again on a pattern instance whose geometry differs.</summary>
    public BlendSpec? Blend { get; init; }
}

/// <summary>
/// A blend as specified (edge references, size, options) and the edges it was made on (start, middle and end
/// point of each): a pattern copies the tool where an instance's edges are the seed's edges moved, and solves the
/// blend again where they are not (e.g. a groove patterned onto a fillet: the fillet on its rim runs differently).
/// </summary>
public sealed record BlendSpec(List<FeatherCad.Core.Model.EdgeRef> Refs, double Size, bool Fillet, bool Propagate, bool Overflow,
    FeatherCad.Kernel.ChamferSize? Chamfer, List<FeatherCad.Kernel.Vec3[]> Edges, double Length);

/// <summary>
/// A thread shown cosmetically (not modeled): a helix of the given lead on the cylinder of <c>Radius</c> about the
/// axis, from <c>Origin</c> (on the axis, at the thread's start) along <c>Axis</c> for <c>Length</c>, starting at
/// <c>RefDir</c>. External threads (shafts) are seen from outside the cylinder, internal ones (holes) from inside.
/// </summary>
public sealed record CosmeticThread(Vec3 Origin, Vec3 Axis, Vec3 RefDir, double Radius, double Lead, double Length, bool External);

/// <summary>How threads are built: each as it is set, or all modeled / all cosmetic (a view setting, not saved).</summary>
public enum ThreadDisplay { AsSet, Modeled, Cosmetic }

/// <summary>Immutable model state after a feature.</summary>
public sealed class ModelState
{
    public List<Body> Bodies { get; init; } = new();
    public Dictionary<string, Frame> Planes { get; init; } = new();
    public Dictionary<string, ToolInfo> Tools { get; init; } = new();
    public Dictionary<string, SketchResult> Sketches { get; init; } = new();
    /// <summary>Flat patterns by body id (made by a Flat Pattern feature, whether the part shows flat or folded).</summary>
    public Dictionary<string, FlatPattern> Flats { get; init; } = new();
    /// <summary>Cosmetic threads by feature id.</summary>
    public Dictionary<string, CosmeticThread> Cosmetics { get; init; } = new();

    public static ModelState Initial() => new();

    public ModelState With(List<Body>? bodies = null) => new()
    {
        Bodies = bodies ?? Bodies,
        Planes = Planes,
        Tools = Tools,
        Sketches = Sketches,
        Flats = Flats,
        Cosmetics = Cosmetics,
    };

    public ModelState Copy() => new()
    {
        Bodies = new List<Body>(Bodies),
        Planes = new Dictionary<string, Frame>(Planes),
        Tools = new Dictionary<string, ToolInfo>(Tools),
        Sketches = new Dictionary<string, SketchResult>(Sketches),
        Flats = new Dictionary<string, FlatPattern>(Flats),
        Cosmetics = new Dictionary<string, CosmeticThread>(Cosmetics),
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

/// <summary>A rebuild's progress: feature <c>Index</c> of <c>Count</c> is being built; <c>Detail</c> is progress inside it.</summary>
public sealed record RegenProgress(int Index, int Count, string FeatureId, string FeatureName, string? Detail);
