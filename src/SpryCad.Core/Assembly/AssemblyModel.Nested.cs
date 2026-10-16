using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>Where a sub-assembly was opened from.</summary>
public enum SubSource { File, Embedded, Cache }

/// <summary>
/// A sub-assembly as an assembly using it has it (in one configuration): opened as a model of its own, or why it
/// could not be.
/// </summary>
public sealed class SubAssembly
{
    public AssemblyModel? Model { get; init; }
    public SubSource Source { get; init; }
    /// <summary>
    /// Why it is not shown as its file is (not found, could not be read, holds the assembly using it; missing and shown
    /// from the assembly's copy), if so.
    /// </summary>
    public string? Problem { get; init; }
}

/// <summary>
/// A component where it is in the assembly, at any depth: a component of the assembly itself, or of a sub-assembly
/// (of a sub-assembly …). <c>Path</c> names it as mates and the graphics area do: the components' ids from the
/// assembly down ("C3", "C3/C2"). <c>Top</c> is the assembly's own component holding it, <c>Rel</c> its path within
/// <c>Top</c> ("C2"; null for <c>Top</c> itself). <c>Driven</c>: this assembly places it (its own components, and those
/// of flexible sub-assemblies all the way down); the others are where their sub-assembly's file has them.
/// </summary>
/// <para><c>Hidden</c>, <c>Suppressed</c> and <c>Appearance</c>: as this assembly shows it (its own and the sub-assemblies'
/// state, with this assembly's overrides for the instance: <see cref="Component.Overrides"/>).</para>
public sealed record Occurrence(string Path, Component Component, AssemblyModel Owner, Transform World, Component Top, string? Rel, bool Driven,
    bool Hidden = false, bool Suppressed = false, Appearance? Appearance = null)
{
    /// <summary>The part's display (null for a sub-assembly, or a part not shown).</summary>
    public PartDisplay? Display => Component.IsAssembly ? null : Owner.DisplayOf(Component);
    /// <summary>The appearance's color (null: the part's own).</summary>
    public double[]? Color => Appearance?.Color;
}

/// <summary>
/// Where everything is (placements, flexible sub-assemblies' poses and the turns their gears have counted, the
/// assembly's own mates' turns): taken before something moves the assembly, put back to undo it.
/// </summary>
public sealed class PoseState
{
    internal readonly Dictionary<Component, (Placement placement, Dictionary<string, Placement>? poses, Dictionary<string, double[]>? turns)> Components = new();
    internal readonly Dictionary<Mate, (double a, double b)> Turns = new();

    /// <summary>Whether anything moved since it was taken.</summary>
    public bool Moved(AssemblyModel m) =>
        m.Doc.Components.Any(c => !Components.TryGetValue(c, out var was) || !ReferenceEquals(was.placement, c.Placement)
            || !Same(was.poses, c.Poses));

    static bool Same(Dictionary<string, Placement>? a, Dictionary<string, Placement>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || b == null || a.All(kv => b.TryGetValue(kv.Key, out var p) && ReferenceEquals(p, kv.Value)));
}

public sealed partial class AssemblyModel
{
    /// <summary>The assembly using this one as a sub-assembly (null: opened as itself).</summary>
    public AssemblyModel? Parent { get; }
    readonly List<string>? _searchRoots;

    /// <summary>
    /// The sub-assemblies, by <see cref="SubKey"/>: one model per assembly file and configuration (shared by its
    /// components in that configuration).
    /// </summary>
    public Dictionary<string, SubAssembly> Subs { get; } = new();

    public static bool IsAssemblyFile(string path) => System.IO.Path.GetExtension(path).Equals(AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>A sub-assembly component's model key: its file's key, and its configuration when it names one ("key|Open").</summary>
    public static string SubKey(Component c) =>
        ComponentResolver.Key(c.Part) + (c.Configuration != null ? "|" + c.Configuration : "");

    public SubAssembly? SubOf(Component c) => c.IsAssembly ? Subs.GetValueOrDefault(SubKey(c)) : null;

    /// <summary>Whether the assembly <paramref name="id"/> is this one or uses this one (a sub-assembly of it would hold itself).</summary>
    bool Within(string id)
    {
        for (var m = this; m != null; m = m.Parent) if (m.Doc.Id == id) return true;
        return false;
    }

    /// <summary>Some sub-assembly, at any depth, would hold an assembly using it (it is not shown).</summary>
    public bool HasCycle => Subs.Values.Any(s => s.Problem == CycleProblem || s.Model?.HasCycle == true);

    const string CycleProblem = "it holds the assembly using it";

    /// <summary>
    /// (Re)opens a sub-assembly, once for each configuration its components use: from its embedded copy, or its file
    /// (where <see cref="Resolution"/> found it), else (missing) from the copy kept when this assembly was last saved
    /// (<see cref="RefreshCaches"/>). Its own parts found and shown, its own mates solved. A sub-assembly holding an
    /// assembly using it is not opened.
    /// </summary>
    public void LoadSub(string key, bool defer = false)
    {
        var comps = ComponentsOf(key).ToList();
        foreach (var k in Subs.Keys.Where(k => OfPart(k, key) && !comps.Any(c => SubKey(c) == k)).ToList()) Subs.Remove(k);
        if (comps.Count == 0) return;
        // (its embedded copy, or its file: read once)
        var read = new Dictionary<string, (byte[]? bytes, string? file, SubSource src, string? problem)>();
        (byte[]? bytes, string? file, SubSource src, string? problem) Source(PartRef r)
        {
            var which = r.Embedded ?? "";
            if (read.TryGetValue(which, out var had)) return had;
            byte[]? bytes = null; string? file = null; var src = SubSource.File; string? problem = null;
            try
            {
                if (r.Embedded != null) { if (Doc.Files.TryGetValue(r.Embedded, out var eb)) { bytes = eb; src = SubSource.Embedded; file = Path; } }
                else if (Resolution.Parts.GetValueOrDefault(key) is { Status: PartStatus.Found or PartStatus.Moved, Path: { } f }) { bytes = File.ReadAllBytes(f); file = f; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = ex.Message; }
            if (bytes == null && r.PartId != null && Doc.Caches.TryGetValue(AssemblyDocument.CacheKey(r.PartId, r.Revision), out var cn) && Doc.Files.TryGetValue(cn, out var cb))
            {
                bytes = cb; src = SubSource.Cache; file = r.AbsolutePath;
            }
            return read[which] = (bytes, file, src, problem);
        }
        foreach (var g in comps.GroupBy(SubKey))
        {
            var r = g.First().Part;
            var name = System.IO.Path.GetFileName(r.Path ?? r.AbsolutePath ?? r.Embedded ?? "assembly.sasm");
            var (bytes, file, src, problem) = Source(r);
            if (bytes == null) { Subs[g.Key] = new SubAssembly { Problem = problem ?? "not found" }; continue; }
            try
            {
                var doc = AssemblyContainer.Load(bytes, name);
                if (doc.Id != null && Within(doc.Id))
                {
                    Subs[g.Key] = new SubAssembly { Problem = CycleProblem };
                    Notes.Add($"{name} is not shown: {CycleProblem}.");
                    continue;
                }
                var config = g.First().Configuration;
                if (config != null && !doc.ActivateConfiguration(config)) Notes.Add($"{name} has no configuration \"{config}\": shown as saved.");
                var sub = Open(doc, file, Index, _searchRoots, defer, this);
                if (sub.HasMates) sub.SolveMates();
                Notes.AddRange(sub.Notes.Select(n => $"{System.IO.Path.GetFileNameWithoutExtension(name)}: {n}"));
                Subs[g.Key] = new SubAssembly
                {
                    Model = sub, Source = src,
                    Problem = src == SubSource.Cache ? (problem ?? "not found") + ": shown from the assembly's copy" : null,
                };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Subs[g.Key] = new SubAssembly { Problem = ex.Message };
            }
        }
    }

    /// <summary>
    /// Adds a component of the assembly at <paramref name="path"/> (a sub-assembly, linked to its file): rigid, or
    /// <paramref name="flexible"/> (its mates solved with this assembly's, so its components move). An assembly that
    /// is this one, or holds it, cannot be added.
    /// </summary>
    public Component InsertAssembly(string path, Transform placement, bool flexible = false, bool embed = false)
    {
        path = System.IO.Path.GetFullPath(path);
        var doc = AssemblyDocument.Load(path);
        if (doc.Id == null) throw new InvalidDataException("The assembly has not been saved with an identity: open and save it first.");
        if (Path != null && PartIndex.SamePath(Path, path) || Within(doc.Id)) throw new InvalidOperationException("An assembly cannot hold itself.");
        Index?.Record(doc.Id, doc.Revision, path);
        var r = new PartRef { PartId = doc.Id, Revision = doc.Revision, AbsolutePath = path, Assembly = true };
        var dir = Path != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) : null;
        r.Path = dir != null ? ComponentResolver.Relative(dir, path) : System.IO.Path.GetFileName(path);
        // the same reference as the assembly's other components of it (linked, or one embedded copy)
        if (Doc.Components.Select(c => c.Part).FirstOrDefault(p => p.Assembly && p.PartId == doc.Id && (p.Embedded != null) == embed) is { } same) r = same.Clone();
        else if (embed)
        {
            r.Embedded = UniqueName("subs/" + SafeFileName(System.IO.Path.GetFileNameWithoutExtension(path)) + AssemblyDocument.FileExtension);
            Doc.Files[r.Embedded] = File.ReadAllBytes(path);
        }
        var key = ComponentResolver.Key(r);
        if (!Resolution.Parts.ContainsKey(key)) Resolution.Parts[key] = new PartResolution(embed ? PartStatus.Embedded : PartStatus.Found, embed ? null : path, false, null);
        var c = Doc.AddComponent(r, placement, System.IO.Path.GetFileNameWithoutExtension(path));
        c.Flexible = flexible;
        if (!Subs.ContainsKey(SubKey(c)))
        {
            var sub = Open(doc, embed ? Path : path, Index, _searchRoots, false, this);
            if (sub.HasCycle)
            {
                Doc.Components.Remove(c);
                if (embed && !Doc.Components.Any(x => x.Part.Embedded == r.Embedded)) Doc.Files.Remove(r.Embedded!);
                throw new InvalidOperationException("It holds this assembly: an assembly cannot hold itself.");
            }
            if (sub.HasMates) sub.SolveMates();
            Notes.AddRange(sub.Notes.Select(n => $"{System.IO.Path.GetFileNameWithoutExtension(path)}: {n}"));
            Subs[SubKey(c)] = new SubAssembly { Model = sub, Source = embed ? SubSource.Embedded : SubSource.File };
        }
        return c;
    }

    /// <summary>
    /// Makes a sub-assembly flexible (its components start where its file has them, then move with this assembly's
    /// mates) or rigid (as its file has it). Mates are not solved here.
    /// </summary>
    public void SetFlexible(Component c, bool flexible)
    {
        if (!c.IsAssembly || c.Flexible == flexible) return;
        c.Flexible = flexible;
        c.Poses = null;
        c.Turns = null;
    }

    /// <summary>
    /// The displays and sub-assemblies as the components now need them (after the document was replaced: undo, redo):
    /// those no component uses dropped, those missing loaded.
    /// </summary>
    public void Sync()
    {
        foreach (var key in Parts.Keys.Where(k => !ComponentsShowing(k).Any()).ToList()) Parts.Remove(key);
        foreach (var key in Subs.Keys.Where(k => !Doc.Components.Any(c => c.IsAssembly && SubKey(c) == k)).ToList()) Subs.Remove(key);
        foreach (var g in Doc.Components.GroupBy(c => ComponentResolver.Key(c.Part)).ToList())
        {
            if (g.First().IsAssembly) { if (g.Any(c => !Subs.ContainsKey(SubKey(c)))) LoadSub(g.Key); }
            else if (g.Any(c => !Parts.ContainsKey(DisplayKey(c)))) LoadPart(g.Key);
        }
        ApplyPatterns();
    }

    // ------------------------------------------------------------------------------------------
    // keeping up with the files

    /// <summary>
    /// The files the assembly shows, at every depth: its parts' and sub-assemblies' (where they were found; embedded
    /// and missing ones have none), and theirs. What to watch for changes.
    /// </summary>
    public IEnumerable<string> SourceFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Resolution.Parts.Values)
            if (r is { Status: PartStatus.Found or PartStatus.Moved, Path: { } p } && seen.Add(p)) yield return p;
        foreach (var sub in Subs.Values)
            if (sub.Model != null)
                foreach (var p in sub.Model.SourceFiles())
                    if (seen.Add(p)) yield return p;
    }

    /// <summary>
    /// Shows again what changed among <paramref name="files"/> (saved since it was shown: a new revision), at any
    /// depth: a part's displays rebuilt (with <paramref name="defer"/>: later, as when opening), a sub-assembly opened
    /// again (its parts as they now are, its own mates solved). Files saved again unchanged, still being written, or
    /// now holding something else are left alone; nothing else is read. Mates are not solved here. The names of what was
    /// shown again ("PinSleeve&lt;1&gt;/Pin&lt;1&gt;": one component of each).
    /// </summary>
    public List<string> ReloadChanged(IReadOnlyCollection<string> files, bool defer = true)
    {
        var changed = new List<string>();
        if (files.Count == 0) return changed;
        bool Listed(string path) => files.Any(f => PartIndex.SamePath(f, path));
        foreach (var g in Doc.Components.GroupBy(c => ComponentResolver.Key(c.Part)).ToList())
        {
            var first = g.First();
            if (Resolution.Parts.GetValueOrDefault(g.Key) is not { Status: PartStatus.Found or PartStatus.Moved, Path: { } file } || !Listed(file)) continue;
            var (id, rev) = DocumentContainer.ReadIdentity(file);
            // (being written, or another file now: left as it is)
            if (id == null || first.Part.PartId != null && id != first.Part.PartId) continue;
            if (first.IsAssembly)
            {
                if (Subs.Where(kv => OfPart(kv.Key, g.Key)).All(kv => kv.Value.Model?.Doc.Revision == rev)) continue;
                LoadSub(g.Key, defer);
            }
            else
            {
                // (shown as that revision already, or being built from it)
                var shown = Parts.Where(kv => OfPart(kv.Key, g.Key)).Select(kv => kv.Value).ToList();
                if (shown.Count > 0 && shown.All(pd => (pd.Building ? pd.Part?.Revision : pd.Revision) == rev)) continue;
                LoadPart(g.Key, defer);
            }
            changed.Add(first.Name);
        }
        // what changed inside the sub-assemblies (those just opened again are as their files are)
        foreach (var c in Doc.Components.Where(c => c.IsAssembly).GroupBy(SubKey).Select(g => g.First()))
            if (!changed.Contains(c.Name) && SubOf(c)?.Model is { } sm)
            {
                var inner = sm.ReloadChanged(files, defer);
                if (inner.Count == 0) continue;
                // its parts changed: its own mates solved again (a rigid one shows as its parts now hold)
                if (sm.HasMates) sm.SolveMates();
                changed.AddRange(inner.Select(n => c.Name + "/" + n));
            }
        return changed;
    }

    /// <summary>Whether a model is this one or a sub-assembly of it now (not one replaced since).</summary>
    bool Holds(AssemblyModel m) => m == this || Subs.Values.Any(s => s.Model != null && s.Model.Holds(m));

    /// <summary>Displays waiting to be built here or in a sub-assembly.</summary>
    public bool AnyPending => Pending.Count > 0 || Subs.Values.Any(s => s.Model?.AnyPending == true);

    /// <summary>The assembly has mates to solve: its own, or a flexible sub-assembly's.</summary>
    public bool HasMates => Doc.Mates.Count > 0 || Doc.Components.Any(c => c is { IsAssembly: true, Flexible: true, Suppressed: false } && SubOf(c)?.Model?.HasMates == true);

    // ------------------------------------------------------------------------------------------
    // occurrences: components at any depth, by path

    /// <summary>A component's placement relative to the assembly holding it, as this assembly has it.</summary>
    static Placement PoseOf(Component c, Component top, string? rel, bool driven) =>
        rel != null && driven && top.Poses != null && top.Poses.TryGetValue(rel, out var p) ? p : c.Placement;

    /// <summary>
    /// Every component at every depth, where it is: the assembly's own in order, each sub-assembly followed by what it
    /// holds. Suppressed ones (and what they hold) are left out unless <paramref name="suppressed"/>.
    /// </summary>
    public IEnumerable<Occurrence> Occurrences(bool suppressed = false)
    {
        foreach (var c in Doc.Components)
            foreach (var o in Walk(Make(this, c, null), suppressed))
                yield return o;
    }

    static IEnumerable<Occurrence> Walk(Occurrence o, bool suppressed)
    {
        if (o.Suppressed && !suppressed) yield break;
        yield return o;
        if (o.Owner.SubOf(o.Component)?.Model is not { } sm) yield break;
        foreach (var cc in sm.Doc.Components)
            foreach (var x in Walk(Make(sm, cc, o), suppressed))
                yield return x;
    }

    /// <summary>
    /// A component as an occurrence: one of the assembly's own (<paramref name="parent"/> null), or one of the
    /// sub-assembly <paramref name="parent"/> (its overrides from the instance holding it applied).
    /// </summary>
    static Occurrence Make(AssemblyModel owner, Component c, Occurrence? parent)
    {
        if (parent == null) return new Occurrence(c.Id, c, owner, c.Placement.ToTransform(), c, null, true, c.Hidden, c.Suppressed, c.Look);
        var top = parent.Top;
        var rel = parent.Rel == null ? c.Id : parent.Rel + "/" + c.Id;
        var driven = parent.Driven && parent.Component.Flexible;
        var ov = top.Overrides?.GetValueOrDefault(rel);
        return new Occurrence(parent.Path + "/" + c.Id, c, owner, parent.World * PoseOf(c, top, rel, driven).ToTransform(), top, rel, driven,
            parent.Hidden || (ov?.Hidden ?? c.Hidden), parent.Suppressed || (ov?.Suppressed ?? c.Suppressed), ov?.Look ?? c.Look ?? parent.Appearance);
    }

    /// <summary>The parts shown, where they are (suppressed ones left out; hidden ones too unless <paramref name="hidden"/>).</summary>
    public IEnumerable<(Occurrence o, PartDisplay pd)> PlacedParts(bool hidden = false)
    {
        foreach (var o in Occurrences())
            if ((hidden || !o.Hidden) && o.Display is { } pd) yield return (o, pd);
    }

    /// <summary>The component at a path ("C3", "C3/C2"), where it is; null if there is none.</summary>
    public Occurrence? Find(string? path)
    {
        if (path == null) return null;
        var segs = path.Split('/');
        if (Doc.Find(segs[0]) is not { } top) return null;
        var o = Make(this, top, null);
        for (int i = 1; i < segs.Length; i++)
        {
            if (o.Owner.SubOf(o.Component)?.Model is not { } sm || sm.Doc.Find(segs[i]) is not { } c) return null;
            o = Make(sm, c, o);
        }
        return o;
    }

    /// <summary>The display of the part at a path (null: a sub-assembly, or nothing there).</summary>
    public PartDisplay? DisplayAt(string path) => Find(path)?.Display;

    static string? ParentPath(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : null;

    /// <summary>The component at a path by its names from the assembly down ("PinSleeve&lt;1&gt;/Sleeve&lt;1&gt;").</summary>
    public string NameAt(string path)
    {
        var names = new List<string>();
        for (var p = path; p != null; p = ParentPath(p)) names.Insert(0, Find(p)?.Component.Name ?? "?");
        return string.Join("/", names);
    }

    /// <summary>
    /// What a click on the component at a path selects: itself when this assembly places it (its own components, those
    /// of flexible sub-assemblies), else the rigid sub-assembly it is part of (which moves as one).
    /// </summary>
    public string Selectable(string path)
    {
        for (var p = path; p != null; p = ParentPath(p))
            if (Find(p) is { Driven: true }) return p;
        return path;
    }

    /// <summary>
    /// What moves when the component at a path is dragged or mated: the component itself when this assembly places
    /// it and it floats; else what carries it (a rigid sub-assembly, or the one holding a fixed component of a
    /// flexible one), if that floats. Null when nothing there can move.
    /// </summary>
    public string? Movable(string? path)
    {
        for (var p = path; p != null; p = ParentPath(p))
        {
            if (Find(p) is not { } o) return null;
            if (o.Rel == null) return o.Component.Fixed ? null : p;
            if (o.Driven && !o.Component.Fixed) return p;
        }
        return null;
    }

    /// <summary>Everything that can move: the floating components and those of flexible sub-assemblies.</summary>
    public IEnumerable<string> MovableComponents() =>
        Occurrences().Where(o => !o.Component.Fixed && (o.Rel == null || o.Driven)).Select(o => o.Path);

    /// <summary>Where the component at a path is in the assembly.</summary>
    public Transform WorldOf(string path) => Find(path)?.World ?? Transform.Identity;

    /// <summary>
    /// Puts a component (one this assembly places: <see cref="Movable"/>) at a placement in the assembly: the
    /// assembly's own component moved, or a flexible sub-assembly's posed there for this instance.
    /// </summary>
    public void SetWorld(string path, Transform world)
    {
        if (Find(path) is not { } o) return;
        if (o.Rel == null) { o.Component.Placement = Placement.From(world); return; }
        if (!o.Driven) return;
        (o.Top.Poses ??= new())[o.Rel] = Placement.From(WorldOf(ParentPath(path)!).RigidInverse() * world);
    }

    /// <summary>The box around the parts at and under a path, in the assembly (empty if none is shown).</summary>
    public Box3 WorldBounds(string path)
    {
        var b = Box3.Empty;
        foreach (var (o, pd) in PlacedParts())
        {
            if (o.Path != path && !o.Path.StartsWith(path + "/", StringComparison.Ordinal) || pd.Mesh.Bounds.IsEmpty) continue;
            var mb = pd.Mesh.Bounds;
            for (int k = 0; k < 8; k++)
                b.Add(o.World.Point(new Vec3((k & 1) == 0 ? mb.Min.X : mb.Max.X, (k & 2) == 0 ? mb.Min.Y : mb.Max.Y, (k & 4) == 0 ? mb.Min.Z : mb.Max.Z)));
        }
        return b;
    }

    /// <summary>Where everything is now (<see cref="Restore"/> puts it back).</summary>
    public PoseState CapturePoses()
    {
        var s = new PoseState();
        foreach (var c in Doc.Components)
            s.Components[c] = (c.Placement, c.Poses == null ? null : new(c.Poses), c.Turns == null ? null : new(c.Turns));
        foreach (var m in Doc.Mates) s.Turns[m] = (m.AngleA, m.AngleB);
        return s;
    }

    public void Restore(PoseState s)
    {
        foreach (var c in Doc.Components)
            if (s.Components.TryGetValue(c, out var was))
            {
                c.Placement = was.placement;
                c.Poses = was.poses == null ? null : new(was.poses);
                c.Turns = was.turns == null ? null : new(was.turns);
            }
        // the gears' and screws' counted turns as they were (else they would count from where they were left)
        foreach (var m in Doc.Mates) if (s.Turns.TryGetValue(m, out var t)) (m.AngleA, m.AngleB) = t;
    }

    // ------------------------------------------------------------------------------------------
    // solving with sub-assemblies

    /// <summary>
    /// The mates solved. With sub-assemblies, over the assembly flattened: every component at every depth a component
    /// of the solve (named by its path); a rigid sub-assembly carries all it holds, a flexible one its fixed
    /// components, while its floating ones move for themselves under its own mates (solved with the assembly's).
    /// Their new places go back as the assembly's components' placements and the flexible sub-assemblies' poses.
    /// </summary>
    MateSolver.Result Solve(ISet<string>? onlyMove, MateSolver.Pull? pull)
    {
        if (Flatten() is not { } f)
            return pull != null ? MateSolver.SolveDrag(Doc, Local, pull) : MateSolver.Solve(Doc, Local, onlyMove);
        var res = pull != null ? MateSolver.SolveDrag(f.Doc, Local, f.PullOnBody(pull), f.Attached) : MateSolver.Solve(f.Doc, Local, onlyMove, 80, f.Attached);
        WriteBack(f, res);
        return res;
    }

    /// <summary>
    /// The assembly flattened for the solver: every component at every depth (named by its path), what carries what,
    /// the flexible sub-assemblies' own mates on their components' paths.
    /// </summary>
    sealed class Flat
    {
        public AssemblyDocument Doc = new();
        public Dictionary<string, MateSolver.Attachment> Attached = new();
        public List<Occurrence> Occs = new();
        public Dictionary<string, Transform> World = new();
        public List<(Mate mate, Component top, string key)> SubMates = new();

        /// <summary>A pull on a carried component pulls what carries it.</summary>
        public MateSolver.Pull PullOnBody(MateSolver.Pull pull) =>
            Attached.TryGetValue(pull.Component, out var at) ? pull with { Component = at.Body, Local = at.Offset.Point(pull.Local) } : pull;
    }

    /// <summary>The assembly flattened (null without sub-assemblies: the solver works on the assembly itself).</summary>
    Flat? Flatten()
    {
        if (!Doc.Components.Any(c => c.IsAssembly && !c.Suppressed)) return null;
        var f = new Flat();
        foreach (var o in Occurrences())
        {
            f.Occs.Add(o);
            f.World[o.Path] = o.World;
            f.Doc.Components.Add(new Component { Id = o.Path, Fixed = o.Rel == null && o.Component.Fixed, Placement = Placement.From(o.World) });
            // placed by this assembly and floating: moves for itself; else carried by what holds it
            if (o.Rel != null && !(o.Driven && !o.Component.Fixed))
            {
                var parent = ParentPath(o.Path)!;
                var body = f.Attached.TryGetValue(parent, out var pa) ? pa.Body : parent;
                f.Attached[o.Path] = new MateSolver.Attachment(body, f.World[body].RigidInverse() * o.World);
            }
            // a flexible sub-assembly's own mates, on its components' paths (its origin planes: the sub-assembly's)
            if (o.Component is { IsAssembly: true, Flexible: true } && o.Driven && o.Owner.SubOf(o.Component)?.Model is { } sm)
                foreach (var m in sm.Doc.Mates)
                {
                    var c = m.Clone();
                    var key = (o.Rel == null ? "" : o.Rel + "/") + m.Id;
                    c.Id = o.Path + "/" + m.Id;
                    c.A.Component = c.A.Component == null ? o.Path : o.Path + "/" + c.A.Component;
                    c.B.Component = c.B.Component == null ? o.Path : o.Path + "/" + c.B.Component;
                    if (o.Top.Turns != null && o.Top.Turns.TryGetValue(key, out var tr) && tr.Length == 2) (c.AngleA, c.AngleB) = (tr[0], tr[1]);
                    f.Doc.Mates.Add(c);
                    f.SubMates.Add((c, o.Top, key));
                }
        }
        // the assembly's own mates first (themselves: their turns settle as always)
        f.Doc.Mates.InsertRange(0, Doc.Mates);
        return f;
    }

    /// <summary>
    /// A solve of the flattened assembly back into it: the assembly's components' placements, the flexible
    /// sub-assemblies' poses and their gears' and screws' turns.
    /// </summary>
    void WriteBack(Flat f, MateSolver.Result res)
    {
        foreach (var fc in f.Doc.Components) f.World[fc.Id] = fc.Placement.ToTransform();
        foreach (var o in f.Occs)
        {
            if (o.Rel == null)
            {
                if (res.Moved.Contains(o.Path)) o.Component.Placement = f.Doc.Find(o.Path)!.Placement;
                continue;
            }
            // a flexible sub-assembly's component solved for: where it is now, relative to what holds it (which may
            // have moved without it)
            if (!res.Solved.Contains(o.Path)) continue;
            var pose = f.World[ParentPath(o.Path)!].RigidInverse() * f.World[o.Path];
            var was = PoseOf(o.Component, o.Top, o.Rel, true).ToTransform();
            if (Differs(was, pose)) (o.Top.Poses ??= new())[o.Rel] = Placement.From(pose);
        }
        foreach (var (m, top, key) in f.SubMates)
        {
            if (m.Type is not (MateType.Gear or MateType.Screw)) continue;
            if (top.Turns != null && top.Turns.TryGetValue(key, out var t) && t.Length == 2 && t[0] == m.AngleA && t[1] == m.AngleB) continue;
            (top.Turns ??= new())[key] = new[] { m.AngleA, m.AngleB };
        }
    }

    static bool Differs(in Transform a, in Transform b) =>
        Vec3.Distance(a.T, b.T) > 1e-12 || Math.Abs(a.M00 - b.M00) + Math.Abs(a.M01 - b.M01) + Math.Abs(a.M02 - b.M02) + Math.Abs(a.M10 - b.M10)
            + Math.Abs(a.M11 - b.M11) + Math.Abs(a.M12 - b.M12) + Math.Abs(a.M20 - b.M20) + Math.Abs(a.M21 - b.M21) + Math.Abs(a.M22 - b.M22) > 1e-12;
}
