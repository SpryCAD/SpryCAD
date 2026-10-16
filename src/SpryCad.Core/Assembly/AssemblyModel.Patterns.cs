using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// Component patterns (<see cref="ComponentPattern"/>): linear, circular and mirror copies of seed components, placed
/// from their seeds whenever those move (<see cref="ApplyPatterns"/>, after every solve). The copies are fixed
/// (nothing but the pattern moves them).
/// </summary>
public sealed partial class AssemblyModel
{
    /// <summary>
    /// Adds a pattern of seed components (the assembly's own): <paramref name="reference"/> gives the direction (a plane's
    /// normal or an axis: Linear), the axis (Circular) or the plane (Mirror); <paramref name="count"/> copies in all
    /// (the seeds counted) <paramref name="spacing"/> apart (mm, or degrees). Its copies made and placed.
    /// </summary>
    public ComponentPattern AddPattern(PatternKind kind, IEnumerable<Component> seeds, MateRef reference, int count = 3, double spacing = 50, bool reverse = false, int symmetry = -1)
    {
        var list = seeds.Where(s => Doc.Components.Contains(s) && s.Pattern == null).Select(s => s.Id).Distinct().ToList();
        if (list.Count == 0) throw new ArgumentException("Pick components of this assembly to pattern (not pattern copies).", nameof(seeds));
        var p = new ComponentPattern
        {
            Id = "P" + Doc.NextPatternId++, Kind = kind, Seeds = list, Reference = reference, Count = Math.Max(2, count), Spacing = spacing,
            Reverse = reverse, Symmetry = symmetry,
        };
        var name = kind switch { PatternKind.Linear => "LocalLPattern", PatternKind.Circular => "LocalCirPattern", _ => "MirrorComponent" };
        int n = 1;
        while (Doc.Patterns?.Any(x => x.Name == name + n) == true) n++;
        p.Name = name + n;
        (Doc.Patterns ??= new()).Add(p);
        UpdatePattern(p);
        return p;
    }

    /// <summary>A pattern's copies made (or dropped) to match its count and seeds, and placed.</summary>
    public void UpdatePattern(ComponentPattern p)
    {
        p.Count = Math.Max(2, p.Count);
        var copies = p.Kind == PatternKind.Mirror ? 1 : p.Count - 1;
        var keep = new List<PatternInstance>();
        foreach (var seedId in p.Seeds)
        {
            if (Doc.Find(seedId) is not { } seed) continue;
            for (int k = 1; k <= copies; k++)
            {
                var inst = p.Instances.FirstOrDefault(i => i.Seed == seedId && i.Index == k);
                if (inst != null && Doc.Find(inst.Component) is { } existing && ComponentResolver.Key(existing.Part) == ComponentResolver.Key(seed.Part))
                {
                    keep.Add(inst);
                    continue;
                }
                var c = Doc.AddComponent(seed.Part.Clone(), seed.Placement.ToTransform(), BaseName(seed.Name));
                c.Fixed = true;
                c.Pattern = p.Id;
                c.Parameters = seed.Parameters == null ? null : new Dictionary<string, string>(seed.Parameters);
                c.Configuration = seed.Configuration;
                c.Flexible = seed.Flexible;
                c.SetLook(seed.Look);
                keep.Add(new PatternInstance { Seed = seedId, Index = k, Component = c.Id });
            }
        }
        // copies no longer wanted (fewer, or of a seed taken out) removed, their mates with them
        foreach (var gone in p.Instances.Where(i => !keep.Contains(i)).ToList())
            if (Doc.Find(gone.Component) is { } gc) Remove(gc);
        p.Instances = keep;
        Sync();
        ApplyPatterns();
    }

    /// <summary>
    /// Removes a pattern: its copies with it (and their mates), or with <paramref name="keepCopies"/> left as components of
    /// their own, fixed where they are.
    /// </summary>
    public void RemovePattern(ComponentPattern p, bool keepCopies = false)
    {
        Doc.Patterns?.Remove(p);
        foreach (var i in p.Instances)
        {
            if (Doc.Find(i.Component) is not { } c) continue;
            if (keepCopies) c.Pattern = null;
            else Remove(c);
        }
        if (Doc.Patterns is { Count: 0 }) Doc.Patterns = null;
    }

    /// <summary>The pattern a component is a seed or a copy of, if any.</summary>
    public ComponentPattern? PatternOf(Component c) => Doc.Patterns?.FirstOrDefault(p => p.Id == c.Pattern || p.Seeds.Contains(c.Id));

    /// <summary>Every pattern's copies placed from their seeds as they are now. True if any moved.</summary>
    public bool ApplyPatterns()
    {
        if (Doc.Patterns is not { Count: > 0 } ps) return false;
        bool moved = false;
        foreach (var p in ps)
        {
            var g = World(p.Reference);
            var d = (g.D.Length > 1e-12 ? g.D.Normalized : Vec3.UnitZ) * (p.Reverse ? -1 : 1);
            foreach (var i in p.Instances)
            {
                if (Doc.Find(i.Seed) is not { } seed || Doc.Find(i.Component) is not { } c) continue;
                var s = seed.Placement.ToTransform();
                var t = p.Kind switch
                {
                    PatternKind.Linear => Transform.Translation(d * (p.Spacing * i.Index)) * s,
                    PatternKind.Circular => Transform.Rotation(g.P, d, p.Spacing * i.Index * Math.PI / 180) * s,
                    _ => Transform.Mirror(Plane.FromPointNormal(g.P, g.D.Normalized)) * s * LocalMirror(seed, p.Symmetry),
                };
                if (!Differs(t, c.Placement.ToTransform())) continue;
                c.Placement = Placement.From(t);
                moved = true;
            }
        }
        return moved;
    }

    /// <summary>
    /// The reflection of a seed's part in one of its own planes (0: YZ, 1: ZX, 2: XY; -1: the one its shape is most
    /// symmetric about): mirrored in the plane picked and then in this, a symmetric part is only turned.
    /// </summary>
    Transform LocalMirror(Component seed, int symmetry)
    {
        var axis = symmetry is >= 0 and <= 2 ? symmetry : 0;
        if (symmetry < 0)
        {
            var b = seed.IsAssembly ? WorldBounds(seed.Id) is { IsEmpty: false } wb ? Box3Of(seed, wb) : Box3.Empty : DisplayOf(seed)?.Mesh.Bounds ?? Box3.Empty;
            if (!b.IsEmpty)
            {
                double Off(double lo, double hi) => Math.Abs(lo + hi) / Math.Max(1e-9, hi - lo);
                var offs = new[] { Off(b.Min.X, b.Max.X), Off(b.Min.Y, b.Max.Y), Off(b.Min.Z, b.Max.Z) };
                axis = Array.IndexOf(offs, offs.Min());
            }
        }
        return new Transform(axis == 0 ? -1 : 1, 0, 0, 0, axis == 1 ? -1 : 1, 0, 0, 0, axis == 2 ? -1 : 1, Vec3.Zero);
    }

    /// <summary>A box in the assembly brought into a component's own coordinates (its corners').</summary>
    static Box3 Box3Of(Component c, Box3 world)
    {
        var inv = c.Placement.ToTransform().RigidInverse();
        var b = Box3.Empty;
        for (int k = 0; k < 8; k++)
            b.Add(inv.Point(new Vec3((k & 1) == 0 ? world.Min.X : world.Max.X, (k & 2) == 0 ? world.Min.Y : world.Max.Y, (k & 4) == 0 ? world.Min.Z : world.Max.Z)));
        return b;
    }

    // ------------------------------------------------------------------------------------------
    // exploded views

    /// <summary>
    /// How far each component an exploded view moves is moved (by path; what it holds goes with it), the view played
    /// to <paramref name="t"/> (0: collapsed, 1: fully exploded; its steps one after another).
    /// </summary>
    public static Dictionary<string, Vec3> ExplodeOffsets(ExplodeView v, double t = 1)
    {
        var offs = new Dictionary<string, Vec3>();
        int n = v.Steps.Count;
        for (int i = 0; i < n; i++)
        {
            var f = Math.Clamp(t * n - i, 0, 1);
            if (f <= 0) break;
            var s = v.Steps[i];
            var d = s.Direction.Length > 1e-12 ? s.Direction.Normalized : Vec3.UnitZ;
            foreach (var c in s.Components) offs[c] = offs.GetValueOrDefault(c) + d * (s.Distance * f);
        }
        return offs;
    }

    /// <summary>The move of the component at a path in an exploded view: its own and those of what holds it.</summary>
    public static Vec3 ExplodeOffsetOf(string path, IReadOnlyDictionary<string, Vec3> offsets)
    {
        var v = Vec3.Zero;
        for (var p = path; p != null; p = ParentPath(p)) v += offsets.GetValueOrDefault(p);
        return v;
    }

    /// <summary>
    /// An exploded view made from where the components are: each of the assembly's own (but the first fixed one, which
    /// stays) moved straight out from the middle of the assembly.
    /// </summary>
    public ExplodeView AutoExplode(string name = "Exploded View1")
    {
        var v = new ExplodeView { Name = name };
        var all = Box3.Empty;
        foreach (var c in Doc.Components) if (!c.Suppressed && WorldBounds(c.Id) is { IsEmpty: false } cb) { all.Add(cb.Min); all.Add(cb.Max); }
        if (all.IsEmpty) return v;
        var ground = Doc.Components.FirstOrDefault(c => c.Fixed && !c.Suppressed);
        var dist = Math.Max(10, all.Diagonal * 0.5);
        foreach (var c in Doc.Components)
        {
            if (c.Suppressed || c == ground || WorldBounds(c.Id) is not { IsEmpty: false } b) continue;
            var d = b.Center - all.Center;
            if (d.Length < 1e-6 * Math.Max(1, all.Diagonal)) d = Vec3.UnitZ;
            v.Steps.Add(new ExplodeStep { Components = new() { c.Id }, Direction = d.Normalized, Distance = dist });
        }
        return v;
    }
}
