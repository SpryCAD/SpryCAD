using System.Text.Json.Serialization;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// How an assembly shows one component of a sub-assembly in it, for this instance only (the sub-assembly's file
/// is not changed): hidden, suppressed, or in another color. Unset values: as the sub-assembly has it.
/// </summary>
public sealed class ComponentOverride
{
    public bool? Hidden { get; set; }
    public bool? Suppressed { get; set; }
    /// <summary>RGB 0-1: the appearance's color, all an older SpryCAD reads (set with <see cref="SetLook"/>).</summary>
    public double[]? Color { get; set; }
    /// <summary>The appearance in full (color, finish, opacity), beside <see cref="Color"/>; read through <see cref="Look"/>.</summary>
    public Appearance? Appearance { get; set; }
    [JsonIgnore] public Appearance? Look => AppearanceOverride.Look(Color, Appearance);
    public void SetLook(Appearance? a) { Appearance = a; Color = a?.Color; }

    [JsonIgnore] public bool IsEmpty => Hidden == null && Suppressed == null && Color == null;
}

/// <summary>An appearance override kept as an older SpryCAD reads it (a color) and in full (an appearance).</summary>
public static class AppearanceOverride
{
    /// <summary>
    /// The override: none without a color; the full appearance with that color (its finish kept if an older
    /// SpryCAD recolored it); a plain color's when there is no full one.
    /// </summary>
    public static Appearance? Look(double[]? color, Appearance? full) =>
        color is not { Length: 3 } c ? null
        : full == null ? new Appearance(c)
        : full.Color.SequenceEqual(c) ? full : full with { Color = c, Name = null };
}

/// <summary>
/// A named state of the assembly (SolidWorks' configurations): which components and mates are suppressed or hidden,
/// each part's configuration and each sub-assembly's. Placements are the assembly's in every one.
/// The active configuration's state is the components' and mates' own (this record of it is brought up to date when
/// another is activated or the assembly is saved).
/// </summary>
public sealed class AssemblyConfiguration
{
    public string Name { get; set; } = "Default";
    /// <summary>Components suppressed (ids of the assembly's own).</summary>
    public List<string>? Suppressed { get; set; }
    public List<string>? Hidden { get; set; }
    /// <summary>Mates suppressed (ids).</summary>
    public List<string>? SuppressedMates { get; set; }
    /// <summary>Parts' configurations (<see cref="Component.Parameters"/>) by component id; components not listed: the part as it is.</summary>
    public Dictionary<string, Dictionary<string, string>>? Parameters { get; set; }
    /// <summary>Sub-assemblies' configurations (<see cref="Component.Configuration"/>) by component id.</summary>
    public Dictionary<string, string>? SubConfigurations { get; set; }
}

/// <summary>One step of an exploded view: components (paths) moved along a direction, by a distance.</summary>
public sealed class ExplodeStep
{
    public List<string> Components { get; set; } = new();
    public Vec3 Direction { get; set; } = Vec3.UnitZ;
    public double Distance { get; set; } = 50;
}

/// <summary>An exploded view: its steps, applied in order (a component in several steps moves by all of them).</summary>
public sealed class ExplodeView
{
    public string Name { get; set; } = "Exploded View1";
    public List<ExplodeStep> Steps { get; set; } = new();
}

public enum PatternKind { Linear, Circular, Mirror }

/// <summary>
/// A component pattern: copies of seed components (the assembly's own) placed along a direction, around an axis, or
/// mirrored across a plane. The copies are components of their own (<see cref="Component.Pattern"/>), placed by the
/// pattern whenever their seeds move (never by mates or dragging).
/// </summary>
public sealed class ComponentPattern
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public PatternKind Kind { get; set; }
    /// <summary>Seed components (ids).</summary>
    public List<string> Seeds { get; set; } = new();
    /// <summary>
    /// The direction (a plane's normal, an axis), the axis (Circular: a round face's or an axis), or the mirror plane: a
    /// mate reference (an origin plane, or a face of a component).
    /// </summary>
    public MateRef Reference { get; set; } = new();
    /// <summary>Linear and circular: how many, the seeds counted.</summary>
    public int Count { get; set; } = 3;
    /// <summary>Linear: mm between copies; circular: degrees between copies.</summary>
    public double Spacing { get; set; } = 50;
    public bool Reverse { get; set; }
    /// <summary>
    /// Mirror: which of each seed part's own planes it is symmetric about (0: YZ, 1: ZX, 2: XY; -1: worked out from its
    /// shape), so a mirrored copy is the same part turned (no opposite-hand part is made).
    /// </summary>
    public int Symmetry { get; set; } = -1;
    /// <summary>The copies: (seed id, copy number from 1) to component id.</summary>
    public List<PatternInstance> Instances { get; set; } = new();
}

public sealed class PatternInstance
{
    public string Seed { get; set; } = "";
    public int Index { get; set; }
    public string Component { get; set; } = "";
}
