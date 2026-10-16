namespace SpryCad.SheetMetal;

/// <summary>
/// A panel's persistent id, made from the feature that made it and that feature's inputs ("EdgeFlange2.E3"), so flat
/// layouts, dimensions and comments stay on it across edits (sheet metal spec section 4).
/// </summary>
public readonly record struct PanelId(string Value) { public override string ToString() => Value; }
public readonly record struct BendId(string Value) { public override string ToString() => Value; }
public readonly record struct CornerId(string Value) { public override string ToString() => Value; }
public readonly record struct HemId(string Value) { public override string ToString() => Value; }

/// <summary>
/// A flat wall of the sheet: its outline in flat-pattern coordinates (the flat is canonical) and where folding puts it.
/// In the flat the sheet's top side (the side facing the punch) is z = 0 and the material is z = −t to 0.
/// </summary>
public sealed record Panel(PanelId Id, Polygon2 FlatOutline, Frame3 FoldedFrame);

/// <summary>
/// A bend joining panel <see cref="A"/> (on the fixed panel's side) to <see cref="B"/>. In the flat its zone is the
/// strip <see cref="Allowance"/> wide centred on <see cref="FlatAxis"/>, B to the axis's left
/// (<see cref="Line2.Normal"/>), A to its right. <see cref="Angle"/> is the bend (deflection) angle, 0 &lt; θ &lt; π;
/// <see cref="Direction"/> Up folds B toward A's top side (the top side inside the bend).
/// </summary>
public sealed record Bend(
    BendId Id,
    PanelId A, PanelId B,
    Line2 FlatAxis,
    double Angle,
    BendDirection Direction,
    double InsideRadius,
    double Allowance,
    string Tooling,
    AllowanceSource Source)
{
    /// <summary>The index of A's outline edge the bend starts at (-1: not on an edge of A's outline).</summary>
    public int EdgeA { get; init; } = -1;
    /// <summary>The index of B's outline edge the bend ends at.</summary>
    public int EdgeB { get; init; } = -1;

    /// <summary>The tangent line on A's side (where the bend starts) in the flat.</summary>
    public Line2 TangentA => FlatAxis.Offset(-Allowance / 2);
    /// <summary>The tangent line on B's side in the flat.</summary>
    public Line2 TangentB => FlatAxis.Offset(Allowance / 2);
}

public enum HemType { Closed, Open, Teardrop, Rolled }

/// <summary>A hem on a panel's edge (sheet metal spec section 8). Its geometry and checks come with the hem feature.</summary>
public sealed record Hem(HemId Id, PanelId Panel, Line2 FlatEdge, HemType Type, double Length, double Gap, double Allowance);

public enum CornerTreatment { Open, Closed, OverlapA, OverlapB, Miter }

/// <summary>Where two or three bend zones meet (sheet metal spec section 7). Found and trimmed by the corner work.</summary>
public sealed record Corner(CornerId Id, IReadOnlyList<BendId> Bends, CornerTreatment Treatment, double Gap);

/// <summary>A bend graph that is not a tree of panels, or is otherwise wrong: said for the user.</summary>
public sealed class BendGraphException(string message) : Exception(message);

/// <summary>
/// A sheet metal body as panels joined by bends (sheet metal spec section 4): the result of its features, made again
/// on every rebuild, never stored. The flat is canonical; folding places each panel by a rigid transform. The bends
/// form a tree rooted at <see cref="FixedPanel"/> (a closed loop of bends is rejected: a closed box is modelled with
/// corners, not a cycle).
/// </summary>
public sealed record BendGraph(
    double Thickness,
    IReadOnlyList<Panel> Panels,
    IReadOnlyList<Bend> Bends,
    IReadOnlyList<Hem> Hems,
    IReadOnlyList<Corner> Corners,
    PanelId FixedPanel)
{
    public BendGraph(double thickness, IReadOnlyList<Panel> panels, IReadOnlyList<Bend> bends, PanelId fixedPanel)
        : this(thickness, panels, bends, Array.Empty<Hem>(), Array.Empty<Corner>(), fixedPanel) { }

    public Panel Panel(PanelId id) => Panels.FirstOrDefault(p => p.Id == id) ?? throw new BendGraphException($"There is no panel {id}.");

    /// <summary>
    /// Checks the graph is a tree of panels rooted at the fixed panel: ids unique, every bend joining two panels there
    /// are, every panel reached, no closed loop of bends, angles and radii in range. Returns the bends in the order a
    /// walk from the fixed panel meets them, each with the panel it leads to.
    /// </summary>
    public IReadOnlyList<(Bend bend, PanelId from, PanelId to)> Walk()
    {
        if (Thickness <= 0) throw new BendGraphException("The sheet has no thickness.");
        var ids = new HashSet<PanelId>();
        foreach (var p in Panels) if (!ids.Add(p.Id)) throw new BendGraphException($"Two panels are named {p.Id}.");
        if (!ids.Contains(FixedPanel)) throw new BendGraphException($"The fixed panel {FixedPanel} is not a panel of the sheet.");
        var bendIds = new HashSet<BendId>();
        foreach (var b in Bends)
        {
            if (!bendIds.Add(b.Id)) throw new BendGraphException($"Two bends are named {b.Id}.");
            if (!ids.Contains(b.A) || !ids.Contains(b.B)) throw new BendGraphException($"{b.Id} joins a panel the sheet does not have.");
            if (b.A == b.B) throw new BendGraphException($"{b.Id} joins {b.A} to itself.");
            if (!(b.Angle > 0 && b.Angle < Math.PI)) throw new BendGraphException($"{b.Id}: a bend angle must be more than 0° and less than 180°.");
            if (b.InsideRadius < 0) throw new BendGraphException($"{b.Id}: the inside radius cannot be negative.");
            if (b.Allowance < 0) throw new BendGraphException($"{b.Id}: the bend allowance cannot be negative.");
        }

        // a walk from the fixed panel, bends taken in their order (deterministic): reaching a panel twice is a loop
        var order = new List<(Bend, PanelId, PanelId)>();
        var reached = new Dictionary<PanelId, BendId?> { [FixedPanel] = null };
        var used = new HashSet<BendId>();
        var queue = new Queue<PanelId>();
        queue.Enqueue(FixedPanel);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var b in Bends)
            {
                if (used.Contains(b.Id) || (b.A != p && b.B != p)) continue;
                var next = b.A == p ? b.B : b.A;
                if (reached.ContainsKey(next))
                    throw new BendGraphException($"{b.Id} closes a loop of bends (back to {next}): bends must form a tree from the fixed panel. Model a closed shape with a corner instead.");
                used.Add(b.Id);
                reached[next] = b.Id;
                order.Add((b, p, next));
                queue.Enqueue(next);
            }
        }
        var lost = Panels.Where(p => !reached.ContainsKey(p.Id)).Select(p => p.Id.Value).ToList();
        if (lost.Count > 0) throw new BendGraphException($"No bend joins {string.Join(", ", lost)} to the fixed panel {FixedPanel}.");
        return order;
    }

    /// <summary>
    /// The graph folded: the fixed panel stays in place, and each other panel goes where its bend from the panel before
    /// it puts it. Exact: a rigid transform per panel.
    /// </summary>
    public BendGraph Fold()
    {
        var frames = new Dictionary<PanelId, Frame3> { [FixedPanel] = Frame3.Identity };
        foreach (var (b, from, to) in Walk())
        {
            // the bend as seen from 'from': walking it from B to A folds the other way round
            var m = BendTransform(b, Thickness);
            frames[to] = from == b.A ? frames[from].Compose(m) : frames[from].Compose(Inverse(m));
        }
        return this with { Panels = Panels.Select(p => p with { FoldedFrame = frames[p.Id] }).ToList() };
    }

    /// <summary>
    /// Where folding <paramref name="b"/> moves B's flat coordinates, A staying in place: B's side moves back by the
    /// allowance (its tangent line onto A's), then turns by the bend angle about the bend's axis, the inside radius
    /// beyond the inside surface (the top side for Up bends, the bottom for Down).
    /// </summary>
    public static Frame3 BendTransform(Bend b, double t)
    {
        var n = b.FlatAxis.Normal;
        var shift = Frame3.Translation(Vec3.From(-n * b.Allowance));
        var tangent = b.TangentA.Point;
        bool up = b.Direction == BendDirection.Up;
        var axisPoint = Vec3.From(tangent, up ? b.InsideRadius : -t - b.InsideRadius);
        var rot = Frame3.Rotation(axisPoint, Vec3.From(b.FlatAxis.Dir), up ? b.Angle : -b.Angle);
        return rot.Compose(shift);
    }

    static Frame3 Inverse(Frame3 f)
    {
        // rotation transposed, translation turned back
        var x = new Vec3(f.X.X, f.Y.X, f.Z.X);
        var y = new Vec3(f.X.Y, f.Y.Y, f.Z.Y);
        var z = new Vec3(f.X.Z, f.Y.Z, f.Z.Z);
        var inv = new Frame3(x, y, z, default);
        return inv with { Origin = -inv.ApplyDir(f.Origin) };
    }
}
