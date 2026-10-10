using System.Text.Json;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// Changing an assembly's structure: components made into a new sub-assembly, a sub-assembly dissolved into its
/// components, a component moved into a sub-assembly or out of one. Mates go where both their components go (else they
/// stay, on the components' new paths); a sub-assembly changed is saved (its file, or its embedded copy), so every
/// assembly using it sees the change.
/// </summary>
public sealed partial class AssemblyModel
{
    static Component CloneComponent(Component c) =>
        JsonSerializer.Deserialize<Component>(JsonSerializer.Serialize(c, Serialization.Compact), Serialization.Compact)!;

    /// <summary>"Bolt" from "Bolt&lt;3&gt;".</summary>
    static string BaseName(string name) => name.LastIndexOf('<') is var i and > 0 ? name[..i] : name;

    static string UniqueComponentName(AssemblyDocument d, string baseName)
    {
        int n = 1;
        while (d.Components.Any(c => c.Name == $"{baseName}<{n}>")) n++;
        return $"{baseName}<{n}>";
    }

    /// <summary>The first segment of a path ("C3" of "C3/C2").</summary>
    static string TopOf(string path) => path.IndexOf('/') is var i and > 0 ? path[..i] : path;

    /// <summary>A path with its first segment renamed ("C3/C2" to "C9/C2").</summary>
    static string Retop(string path, string top) => path.IndexOf('/') is var i and > 0 ? top + path[i..] : top;

    static bool Under(string path, string of) => path == of || path.StartsWith(of + "/", StringComparison.Ordinal);

    static bool IsIdentity(in Transform t) => !Differs(t, Transform.Identity);

    /// <summary>A component's embedded file and display caches copied from one document to another (embedded file renamed if taken).</summary>
    static void CarryFiles(Component c, AssemblyDocument from, AssemblyDocument to)
    {
        if (c.Part.Embedded is { } e && from.Files.TryGetValue(e, out var bytes))
        {
            if (!to.Files.TryGetValue(e, out var there) || !there.AsSpan().SequenceEqual(bytes))
            {
                var name = e;
                if (to.Files.ContainsKey(name))
                {
                    var ext = System.IO.Path.GetExtension(e); var stem = e[..^ext.Length];
                    for (int i = 2; to.Files.ContainsKey(name); i++) name = $"{stem}-{i}{ext}";
                }
                to.Files[name] = bytes;
                c.Part.Embedded = name;
            }
        }
        if (c.Part.PartId == null) return;
        var ck = AssemblyDocument.CacheKey(c.Part.PartId, c.Part.Revision, c.IsAssembly ? "" : Configurations.KeyOf(c.Parameters));
        if (from.Caches.TryGetValue(ck, out var cn) && from.Files.TryGetValue(cn, out var cache) && !to.Caches.ContainsKey(ck))
        {
            to.Caches[ck] = cn;
            to.Files[cn] = cache;
        }
    }

    /// <summary>A sub-assembly's document as saved (its file or embedded copy, in the configuration its file has active).</summary>
    AssemblyDocument LoadSubDoc(Component s, out string? file)
    {
        file = null;
        if (s.Part.Embedded is { } e && Doc.Files.TryGetValue(e, out var bytes)) return AssemblyContainer.Load(bytes, e);
        if (Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(s.Part)) is { Status: PartStatus.Found or PartStatus.Moved, Path: { } f })
        {
            file = f;
            return AssemblyDocument.Load(f);
        }
        throw new InvalidOperationException($"{s.Name}: its file was not found, so it cannot be changed.");
    }

    /// <summary>A sub-assembly's document saved (its file, or its embedded copy here), then shown again.</summary>
    void WriteSub(Component s, AssemblyDocument sd, string? file)
    {
        var m = Open(sd, s.Part.Embedded != null ? Path : file, Index, _searchRoots, false, this);
        if (m.HasMates) m.SolveMates();
        if (s.Part.Embedded is { } e)
        {
            m.RefreshCaches();
            try { sd.Thumbnail = m.RenderThumbnail() ?? sd.Thumbnail; } catch { }
            Doc.Files[e] = AssemblyContainer.ToBytes(sd);
        }
        else m.Save(file!);
        var key = ComponentResolver.Key(s.Part);
        foreach (var c in ComponentsOf(key)) c.Part.Revision = sd.Revision;
        LoadSub(key);
    }

    /// <summary>
    /// A mate reference renamed by a map of the assembly's own components (paths below them follow); unchanged when its
    /// component is not in the map.
    /// </summary>
    static void Rename(MateRef r, IReadOnlyDictionary<string, string> map)
    {
        if (r.Component != null && map.TryGetValue(TopOf(r.Component), out var to)) r.Component = Retop(r.Component, to);
    }

    /// <summary>
    /// Makes components of this assembly a new sub-assembly saved at <paramref name="path"/> (or embedded here): they
    /// keep their places (the sub-assembly sits at the origin), the mates among them go with them, the others stay on
    /// the components' new paths. Rigid, or <paramref name="flexible"/>. Pattern copies and seeds cannot be moved.
    /// The new sub-assembly component.
    /// </summary>
    public Component FormSubAssembly(IReadOnlyCollection<Component> comps, string path, bool flexible = true, bool embed = false)
    {
        if (comps.Count == 0) throw new ArgumentException("No components to make a sub-assembly of.", nameof(comps));
        if (comps.Any(c => c.Pattern != null || Doc.Patterns?.Any(p => p.Seeds.Contains(c.Id)) == true))
            throw new InvalidOperationException("Components of a pattern cannot be made a sub-assembly: delete the pattern first.");
        path = System.IO.Path.GetFullPath(path);
        var set = Doc.Components.Where(comps.Contains).ToList();
        var sd = new AssemblyDocument { Units = Doc.Units };
        var map = new Dictionary<string, string>();
        foreach (var c in set)
        {
            var nc = CloneComponent(c);
            nc.Id = "C" + sd.NextComponentId++;
            nc.Name = UniqueComponentName(sd, BaseName(c.Name));
            CarryFiles(nc, Doc, sd);
            sd.Components.Add(nc);
            map[c.Id] = nc.Id;
        }
        // its ground: a fixed component (the first, if none was)
        var anyFixed = sd.Components.Any(c => c.Fixed);
        if (!anyFixed) sd.Components[0].Fixed = true;
        var subId = "C" + Doc.NextComponentId;
        var inner = map.ToDictionary(kv => kv.Key, kv => kv.Value);
        var outer = map.ToDictionary(kv => kv.Key, kv => subId + "/" + kv.Value);
        bool In(MateRef r) => r.Component != null && map.ContainsKey(TopOf(r.Component));
        foreach (var m in Doc.Mates.ToList())
        {
            bool a = In(m.A), b = In(m.B);
            if (!a && !b) continue;
            if (a && b)
            {
                // both in it: the sub-assembly's own (its name kept)
                var nm = m.Clone();
                Rename(nm.A, inner); Rename(nm.B, inner);
                sd.AddMate(nm);
                Doc.Mates.Remove(m);
            }
            else { Rename(m.A, outer); Rename(m.B, outer); }
        }
        // saved (its references relative to its own place, its caches and preview made)
        byte[]? bytes = null;
        if (embed)
        {
            var sm = Open(sd, Path, Index, _searchRoots, false, this);
            sm.RefreshCaches();
            try { sd.Thumbnail = sm.RenderThumbnail(); } catch { }
            bytes = AssemblyContainer.ToBytes(sd);
        }
        else
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            Open(sd, path, Index, _searchRoots, false, this).Save(path);
        }
        foreach (var c in set) Doc.Components.Remove(c);
        Doc.RenameIds(p => outer.TryGetValue(TopOf(p), out var to) ? Retop(p, to) : p);
        Component sub;
        if (embed)
        {
            var r = new PartRef { PartId = sd.Id, Revision = sd.Revision, Assembly = true, Embedded = UniqueName("subs/" + SafeFileName(System.IO.Path.GetFileNameWithoutExtension(path)) + AssemblyDocument.FileExtension) };
            Doc.Files[r.Embedded] = bytes!;
            Resolution.Parts[ComponentResolver.Key(r)] = new PartResolution(PartStatus.Embedded, null, false, null);
            sub = Doc.AddComponent(r, Transform.Identity, System.IO.Path.GetFileNameWithoutExtension(path));
            sub.Flexible = flexible;
            LoadSub(ComponentResolver.Key(r));
        }
        else sub = InsertAssembly(path, Transform.Identity, flexible);
        sub.Fixed = anyFixed;
        // (its id as the mates were told)
        if (sub.Id != subId) throw new InvalidOperationException("Component ids out of step.");
        // parts no component of this assembly shows any more are dropped (their embedded copies too)
        foreach (var e in set.Select(c => c.Part.Embedded).OfType<string>().Distinct())
            if (!Doc.Components.Any(c => c.Part.Embedded == e)) Doc.Files.Remove(e);
        Sync();
        return sub;
    }

    /// <summary>
    /// Dissolves a sub-assembly into its components (as it shows them: its configuration, this instance's poses and
    /// overrides), each where it is. A flexible one's mates come along; a rigid one's components are locked to its first
    /// (its mates come along suppressed); a fixed sub-assembly's fixed components stay fixed. Mates to its origin planes
    /// are kept only where those are the assembly's own (it sat at the origin). Notes say what was dropped. The new
    /// components.
    /// </summary>
    public List<Component> Dissolve(Component s)
    {
        if (!s.IsAssembly || !Doc.Components.Contains(s)) throw new ArgumentException("Not a sub-assembly of this assembly.", nameof(s));
        if (SubOf(s)?.Model is not { } sm) throw new InvalidOperationException($"{s.Name} is not shown, so it cannot be dissolved.");
        var sd = sm.Doc;
        var atOrigin = IsIdentity(s.Placement.ToTransform());
        var map = new Dictionary<string, string>();
        var made = new List<Component>();
        string Strip(string k, string id) => k[(id.Length + 1)..];
        foreach (var cc in sd.Components)
        {
            var o = Find(s.Id + "/" + cc.Id)!;
            var nc = CloneComponent(cc);
            nc.Id = "C" + Doc.NextComponentId++;
            nc.Name = UniqueComponentName(Doc, BaseName(cc.Name));
            nc.Placement = Placement.From(o.World);
            nc.Pattern = null;
            // this instance's state of it
            if (s.Overrides?.GetValueOrDefault(cc.Id) is { } ov)
            {
                if (ov.Hidden is { } h) nc.Hidden = h;
                if (ov.Suppressed is { } su) nc.Suppressed = su;
                if (ov.Color != null) nc.Color = ov.Color;
            }
            foreach (var (k, v) in s.Overrides ?? new()) if (k.StartsWith(cc.Id + "/", StringComparison.Ordinal)) (nc.Overrides ??= new())[Strip(k, cc.Id)] = v;
            if (s.Flexible)
            {
                foreach (var (k, v) in s.Poses ?? new()) if (k.StartsWith(cc.Id + "/", StringComparison.Ordinal)) (nc.Poses ??= new())[Strip(k, cc.Id)] = v;
                foreach (var (k, v) in s.Turns ?? new()) if (k.StartsWith(cc.Id + "/", StringComparison.Ordinal)) (nc.Turns ??= new())[Strip(k, cc.Id)] = v;
            }
            CarryFiles(nc, sd, Doc);
            if (nc.Part.Embedded == null && nc.Part.AbsolutePath != null && Path != null)
                nc.Part.Path = ComponentResolver.Relative(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, nc.Part.AbsolutePath);
            var key = ComponentResolver.Key(nc.Part);
            if (!Resolution.Parts.ContainsKey(key) && sm.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(cc.Part)) is { } res)
                Resolution.Parts[key] = nc.Part.Embedded != null ? new PartResolution(PartStatus.Embedded, null, false, null) : res;
            Doc.Components.Add(nc);
            map[cc.Id] = nc.Id;
            made.Add(nc);
        }
        // what holds them now
        var fixedOnes = made.Where((c, i) => sd.Components[i].Fixed).ToList();
        void LockTo(Component ground, IEnumerable<Component> others)
        {
            foreach (var c in others)
            {
                var a = new MateRef { Component = c.Id, D = Vec3.UnitZ };
                var b = new MateRef { Component = ground.Id, D = Vec3.UnitZ };
                var m = new Mate { Type = MateType.Lock, A = a, B = b };
                m.Capture(World(a), World(b), PlacementOf(a), PlacementOf(b));
                Doc.AddMate(m);
            }
        }
        if (!s.Flexible)
        {
            foreach (var c in made) c.Fixed = s.Fixed;
            if (!s.Fixed && made.Count > 1) LockTo(made[0], made.Skip(1));
        }
        else
        {
            foreach (var c in made) c.Fixed = s.Fixed && fixedOnes.Contains(c);
            if (!s.Fixed && fixedOnes.Count > 1) LockTo(fixedOnes[0], fixedOnes.Skip(1));
        }
        // its mates, on the new components
        var dropped = 0;
        string? Sub(string? comp) => comp == null ? (atOrigin ? null : "") : map.TryGetValue(TopOf(comp), out var to) ? Retop(comp, to) : "";
        foreach (var m in sd.Mates)
        {
            var nm = m.Clone();
            nm.A.Component = Sub(m.A.Component); nm.B.Component = Sub(m.B.Component);
            if (nm.A.Component == "" || nm.B.Component == "") { dropped++; continue; }
            if (!s.Flexible) nm.Suppressed = true;
            if (s.Turns?.GetValueOrDefault(m.Id) is { Length: 2 } tr) (nm.AngleA, nm.AngleB) = (tr[0], tr[1]);
            nm.Id = ""; nm.Name = m.Name;
            Doc.AddMate(nm);
        }
        // the assembly's mates on it
        foreach (var m in Doc.Mates.ToList())
        {
            foreach (var r in new[] { m.A, m.B })
            {
                if (r.Component == null || !Under(r.Component, s.Id)) continue;
                if (r.Component == s.Id) r.Component = atOrigin ? null : "";
                else
                {
                    var rest = r.Component[(s.Id.Length + 1)..];
                    r.Component = map.TryGetValue(TopOf(rest), out var to) ? Retop(rest, to) : "";
                }
            }
            if (m.A.Component == "" || m.B.Component == "") { Doc.Mates.Remove(m); dropped++; }
        }
        if (dropped > 0) Notes.Add($"{s.Name} dissolved: {dropped} mate{(dropped == 1 ? "" : "s")} to it (its origin planes, or components not shown) dropped.");
        Doc.RenameIds(p => !Under(p, s.Id) ? p : p == s.Id ? null : map.TryGetValue(TopOf(p[(s.Id.Length + 1)..]), out var to) ? Retop(p[(s.Id.Length + 1)..], to) : null);
        Doc.Components.Remove(s);
        if (s.Part.Embedded is { } e && !Doc.Components.Any(c => c.Part.Embedded == e)) Doc.Files.Remove(e);
        Sync();
        return made;
    }

    /// <summary>
    /// Moves one of the assembly's own components into one of its sub-assemblies (saved: every assembly using it gets the
    /// component), where it is now. Mates between it and the sub-assembly's components go into the sub-assembly; the
    /// others stay on its new path. Its new path.
    /// </summary>
    public string MoveInto(Component x, Component s)
    {
        if (x == s || !s.IsAssembly || !Doc.Components.Contains(x) || !Doc.Components.Contains(s)) throw new ArgumentException("Pick a component and a sub-assembly of this assembly.");
        if (x.Pattern != null || Doc.Patterns?.Any(p => p.Seeds.Contains(x.Id)) == true) throw new InvalidOperationException("A pattern's component cannot be moved: delete the pattern first.");
        if (SubOf(s) is not { Model: not null, Source: not SubSource.Cache }) throw new InvalidOperationException($"{s.Name} is not found, so it cannot be changed.");
        // (a sub-assembly moved into one it holds would hold itself)
        if (x.IsAssembly && SubOf(x)?.Model is { } xm && s.Part.PartId != null && (x.Part.PartId == s.Part.PartId || xm.Occurrences(true).Any(o => o.Component.Part.PartId == s.Part.PartId)))
            throw new InvalidOperationException("An assembly cannot hold itself.");
        var sd = LoadSubDoc(s, out var file);
        var nc = CloneComponent(x);
        nc.Id = "C" + sd.NextComponentId++;
        nc.Name = UniqueComponentName(sd, BaseName(x.Name));
        nc.Placement = Placement.From(WorldOf(s.Id).RigidInverse() * x.Placement.ToTransform());
        nc.Fixed = x.Fixed && s.Fixed;
        CarryFiles(nc, Doc, sd);
        sd.Components.Add(nc);
        var outer = new Dictionary<string, string> { [x.Id] = s.Id + "/" + nc.Id };
        bool InX(MateRef r) => r.Component != null && Under(r.Component, x.Id);
        bool InS(MateRef r) => r.Component != null && Under(r.Component, s.Id);
        foreach (var m in Doc.Mates.ToList())
        {
            if (!InX(m.A) && !InX(m.B)) continue;
            if ((InX(m.A) || InS(m.A)) && (InX(m.B) || InS(m.B)))
            {
                var nm = m.Clone();
                foreach (var r in new[] { nm.A, nm.B })
                    r.Component = Under(r.Component!, x.Id) ? Retop(r.Component!, nc.Id) : r.Component == s.Id ? null : r.Component![(s.Id.Length + 1)..];
                sd.AddMate(nm);
                Doc.Mates.Remove(m);
            }
            else { Rename(m.A, outer); Rename(m.B, outer); }
        }
        WriteSub(s, sd, file);
        Doc.Components.Remove(x);
        Doc.RenameIds(p => Under(p, x.Id) ? Retop(p, s.Id + "/" + nc.Id) : p);
        if (x.Part.Embedded is { } e && !Doc.Components.Any(c => c.Part.Embedded == e)) Doc.Files.Remove(e);
        Sync();
        return s.Id + "/" + nc.Id;
    }

    /// <summary>
    /// Moves a component out of a sub-assembly (one of the assembly's own) into this assembly, where it is now (the
    /// sub-assembly saved without it). The sub-assembly's mates on it come along, on the sub-assembly's paths; its fixed
    /// state is kept only if the sub-assembly is fixed. The new component.
    /// </summary>
    public Component MoveOut(string path)
    {
        var segs = path.Split('/');
        if (segs.Length != 2 || Doc.Find(segs[0]) is not { IsAssembly: true } s || Find(path) is not { } o)
            throw new ArgumentException("Pick a component of one of this assembly's sub-assemblies.", nameof(path));
        if (SubOf(s) is not { Model: not null, Source: not SubSource.Cache }) throw new InvalidOperationException($"{s.Name} is not found, so it cannot be changed.");
        var sd = LoadSubDoc(s, out var file);
        var cid = segs[1];
        if (sd.Find(cid) is not { } cc) throw new InvalidOperationException("It is not in the sub-assembly's file (another configuration?).");
        var nc = CloneComponent(cc);
        nc.Id = "C" + Doc.NextComponentId++;
        nc.Name = UniqueComponentName(Doc, BaseName(cc.Name));
        nc.Placement = Placement.From(o.World);
        nc.Fixed = cc.Fixed && s.Fixed;
        nc.Pattern = null;
        if (s.Overrides?.GetValueOrDefault(cid) is { } ov)
        {
            if (ov.Hidden is { } h) nc.Hidden = h;
            if (ov.Suppressed is { } su) nc.Suppressed = su;
            if (ov.Color != null) nc.Color = ov.Color;
        }
        var keys = (s.Overrides?.Keys ?? Enumerable.Empty<string>()).Concat(s.Poses?.Keys ?? Enumerable.Empty<string>()).Concat(s.Turns?.Keys ?? Enumerable.Empty<string>()).Distinct().ToList();
        foreach (var k in keys)
                if (Under(k, cid))
                {
                    var rest = k == cid ? null : k[(cid.Length + 1)..];
                    if (rest != null && s.Overrides?.TryGetValue(k, out var v1) == true) (nc.Overrides ??= new())[rest] = v1;
                    if (rest != null && s.Poses?.TryGetValue(k, out var v2) == true) (nc.Poses ??= new())[rest] = v2;
                    if (rest != null && s.Turns?.TryGetValue(k, out var v3) == true) (nc.Turns ??= new())[rest] = v3;
                    s.Overrides?.Remove(k); s.Poses?.Remove(k); s.Turns?.Remove(k);
                }
        CarryFiles(nc, sd, Doc);
        var sm = SubOf(s)!.Model!;
        var key = ComponentResolver.Key(nc.Part);
        if (!Resolution.Parts.ContainsKey(key) && sm.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(cc.Part)) is { } res)
            Resolution.Parts[key] = nc.Part.Embedded != null ? new PartResolution(PartStatus.Embedded, null, false, null) : res;
        if (nc.Part.Embedded == null && nc.Part.AbsolutePath != null && Path != null)
            nc.Part.Path = ComponentResolver.Relative(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, nc.Part.AbsolutePath);
        Doc.Components.Add(nc);
        // the sub-assembly's mates on it: this assembly's now (the others on the sub-assembly's paths, its origin planes on it)
        foreach (var m in sd.Mates.ToList())
        {
            bool a = m.A.Component != null && Under(m.A.Component, cid), b = m.B.Component != null && Under(m.B.Component, cid);
            if (!a && !b) continue;
            var nm = m.Clone();
            foreach (var r in new[] { nm.A, nm.B })
                r.Component = r.Component == null ? s.Id : Under(r.Component, cid) ? Retop(r.Component, nc.Id) : s.Id + "/" + r.Component;
            if (s.Turns?.GetValueOrDefault(m.Id) is { Length: 2 } tr) (nm.AngleA, nm.AngleB) = (tr[0], tr[1]);
            nm.Id = "";
            Doc.AddMate(nm);
            sd.Mates.Remove(m);
        }
        sd.Components.Remove(cc);
        sd.RenameIds(p => Under(p, cid) ? null : p);
        foreach (var m in Doc.Mates)
            foreach (var r in new[] { m.A, m.B })
                if (r.Component != null && Under(r.Component, path)) r.Component = nc.Id + r.Component[path.Length..];
        Doc.RenameIds(p => Under(p, path) ? nc.Id + p[path.Length..] : p);
        WriteSub(s, sd, file);
        Sync();
        return nc;
    }
}
