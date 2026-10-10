using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// Editing an open assembly beyond placing and mating: what is shown (hide, suppress, color: for a sub-assembly's
/// components as an override of this instance), configurations, references gone missing, replacing a component, and
/// embedded documents saved back.
/// </summary>
public sealed partial class AssemblyModel
{
    // ------------------------------------------------------------------------------------------
    // shown, suppressed, colored

    /// <summary>
    /// Changes how the component at a path is shown: the assembly's own component itself, a sub-assembly's component
    /// as an override for this instance (the sub-assembly's file untouched; an override back to what the sub-assembly
    /// has is dropped).
    /// </summary>
    void Override(string path, Action<Component> own, Action<ComponentOverride, Component> over)
    {
        if (Find(path) is not { } o) return;
        if (o.Rel == null) { own(o.Component); return; }
        var map = o.Top.Overrides ??= new();
        var ov = map.GetValueOrDefault(o.Rel) ?? new ComponentOverride();
        over(ov, o.Component);
        if (ov.Hidden == o.Component.Hidden) ov.Hidden = null;
        if (ov.Suppressed == o.Component.Suppressed) ov.Suppressed = null;
        if (ov.IsEmpty) map.Remove(o.Rel);
        else map[o.Rel] = ov;
        if (map.Count == 0) o.Top.Overrides = null;
    }

    public void SetHidden(string path, bool hidden) => Override(path, c => c.Hidden = hidden, (ov, _) => ov.Hidden = hidden);

    /// <summary>Suppresses (or brings back) the component at a path: not shown, not solved, not counted.</summary>
    public void SetSuppressed(string path, bool suppressed) => Override(path, c => c.Suppressed = suppressed, (ov, _) => ov.Suppressed = suppressed);

    /// <summary>The component at a path in a color of its own (RGB 0-1), or (null) as its part (or sub-assembly) has it.</summary>
    public void SetColor(string path, double[]? color) => Override(path, c => c.Color = color, (ov, _) => ov.Color = color);

    /// <summary>The paths whose overrides are set for a sub-assembly instance (shown in its tree).</summary>
    public IEnumerable<string> OverriddenPaths(Component top) => top.Overrides?.Keys.Select(k => top.Id + "/" + k) ?? Enumerable.Empty<string>();

    // ------------------------------------------------------------------------------------------
    // configurations

    /// <summary>
    /// Makes a configuration the active one (<see cref="AssemblyDocument.ActivateConfiguration"/>): the displays and
    /// sub-assemblies it needs loaded (with <paramref name="defer"/>, built later), the patterns placed. Mates are not
    /// solved here. False if there is no such configuration.
    /// </summary>
    public bool ActivateConfiguration(string name, bool defer = false)
    {
        if (!Doc.ActivateConfiguration(name)) return false;
        SyncDeferred(defer);
        return true;
    }

    /// <summary>A sub-assembly component shown in another of its configurations (null: the one its file has active).</summary>
    public void SetSubConfiguration(Component c, string? configuration, bool defer = false)
    {
        if (!c.IsAssembly || c.Configuration == configuration) return;
        c.Configuration = configuration;
        // (another configuration may show other components: this instance's poses and overrides of them kept)
        LoadSub(ComponentResolver.Key(c.Part), defer);
    }

    /// <summary>The configurations a sub-assembly component can be shown in (its file's).</summary>
    public IReadOnlyList<string> ConfigurationsOf(Component c) => SubOf(c)?.Model?.Doc.ConfigurationNames ?? Array.Empty<string>();

    void SyncDeferred(bool defer)
    {
        foreach (var key in Parts.Keys.Where(k => !ComponentsShowing(k).Any()).ToList()) Parts.Remove(key);
        foreach (var key in Subs.Keys.Where(k => !Doc.Components.Any(c => c.IsAssembly && SubKey(c) == k)).ToList()) Subs.Remove(key);
        foreach (var g in Doc.Components.GroupBy(c => ComponentResolver.Key(c.Part)).ToList())
        {
            if (g.First().IsAssembly) { if (g.Any(c => !Subs.ContainsKey(SubKey(c)))) LoadSub(g.Key, defer); }
            else if (g.Any(c => !Parts.ContainsKey(DisplayKey(c)))) LoadPart(g.Key, defer);
        }
        ApplyPatterns();
    }

    // ------------------------------------------------------------------------------------------
    // references gone missing

    /// <summary>
    /// Why a mate reference no longer finds what it was made on: its component gone (deleted, or no longer in its
    /// sub-assembly), or its face gone from the part (the part changed). Null when it is found, or cannot be told (the
    /// part is shown only from a copy: the face kept with the mate is used).
    /// </summary>
    public string? Broken(MateRef r)
    {
        if (r.Component == null) return null;
        if (Find(r.Component) is not { } o)
        {
            // (inside a sub-assembly that is not shown at all: nothing can be told)
            var top = r.Component.Split('/')[0];
            if (r.Component.Contains('/') && Find(top) is { } t && t.Owner.SubOf(t.Component)?.Model == null) return null;
            return "its component is gone";
        }
        if (o.Component.IsAssembly) return AssemblyModel.OriginPlanes.Contains(r.Face) ? null : $"face {r.Face} is gone";
        if (o.Display is not { Source: PartSource.File or PartSource.Embedded } pd || pd.Building) return null;
        return pd.Mesh.Faces.Any(f => f.Name == r.Face) ? null : $"face {r.Face} is gone";
    }

    /// <summary>The mates with a reference gone (<see cref="Broken"/>), and why.</summary>
    public List<(Mate mate, string why)> BrokenMates()
    {
        var l = new List<(Mate, string)>();
        foreach (var m in Doc.Mates)
            if ((Broken(m.A) ?? Broken(m.B)) is { } why) l.Add((m, why));
        return l;
    }

    // ------------------------------------------------------------------------------------------
    // replacing a component

    /// <summary>
    /// Replaces a component's part or sub-assembly (and with <paramref name="allInstances"/>, every component of the
    /// same one) by the file at <paramref name="path"/>: the same id, place and mates (a mate whose face the new one
    /// lacks shows as broken: <see cref="Broken"/>), linked or embedded as the old one was. Configurations set for the
    /// old one are dropped. The components replaced.
    /// </summary>
    public List<Component> Replace(Component c, string path, bool allInstances = false)
    {
        path = System.IO.Path.GetFullPath(path);
        var oldKey = ComponentResolver.Key(c.Part);
        var targets = allInstances ? ComponentsOf(oldKey).ToList() : new List<Component> { c };
        var embed = c.Part.Embedded != null;
        // the new one inserted (its reference, display or model made), then taken over by each target
        var placeholder = IsAssemblyFile(path) ? InsertAssembly(path, Transform.Identity, c.Flexible, embed) : Insert(path, Transform.Identity, embed);
        Doc.Components.Remove(placeholder);
        Doc.NextComponentId--;
        var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (var t in targets)
        {
            var sameFile = ComponentResolver.Key(placeholder.Part) == oldKey;
            t.Part = placeholder.Part.Clone();
            if (!sameFile)
            {
                t.Parameters = null;
                t.Configuration = null;
                t.Poses = null; t.Turns = null; t.Overrides = null;
            }
            int n = 1;
            while (Doc.Components.Any(x => x != t && x.Name == $"{baseName}<{n}>")) n++;
            t.Name = $"{baseName}<{n}>";
        }
        // an embedded copy no component uses any more is dropped
        if (embed && !Doc.Components.Any(x => x.Part.Embedded == c.Part.Embedded) && c.Part.Embedded != null) Doc.Files.Remove(c.Part.Embedded);
        Sync();
        // mates on the new one: their faces as it has them (where it has them)
        foreach (var m in Doc.Mates)
            foreach (var r in new[] { m.A, m.B })
                if (r.Component != null && targets.Any(t => r.Component == t.Id || r.Component.StartsWith(t.Id + "/", StringComparison.Ordinal))) Local(r);
        return targets;
    }

    // ------------------------------------------------------------------------------------------
    // embedded documents

    /// <summary>
    /// An embedded part or sub-assembly (attachment <paramref name="name"/>) saved again with new contents (edited in a
    /// tab of its own): its components show it as it now is (built now, or with <paramref name="defer"/> later).
    /// Mates are not solved here.
    /// </summary>
    public void UpdateEmbedded(string name, byte[] bytes, bool defer = false)
    {
        if (!Doc.Files.ContainsKey(name)) throw new ArgumentException("No such embedded file.", nameof(name));
        Doc.Files[name] = bytes;
        foreach (var g in Doc.Components.Where(c => c.Part.Embedded == name).GroupBy(c => ComponentResolver.Key(c.Part)).ToList())
        {
            if (g.First().IsAssembly)
            {
                var rev = AssemblyContainer.Load(bytes, name).Revision;
                foreach (var c in g) c.Part.Revision = rev;
                LoadSub(g.Key, defer);
            }
            else
            {
                Parts.Remove(g.Key);
                foreach (var k in Parts.Keys.Where(k => OfPart(k, g.Key)).ToList()) Parts.Remove(k);
                LoadPart(g.Key, defer);
                // its new revision noted now (a display still to be built would not note it when saved)
                foreach (var c in g) if (DisplayOf(c)?.Part?.Revision is { } rev) c.Part.Revision = rev;
            }
        }
    }
}
