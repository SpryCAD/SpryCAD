namespace SpryCad.Core.Assembly;

/// <summary>
/// An assembly's configurations (<see cref="AssemblyConfiguration"/>): the active one's state is the components' and
/// mates' own; the others are kept as records and put on the components when activated.
/// </summary>
public sealed partial class AssemblyDocument
{
    public const string DefaultConfiguration = "Default";

    /// <summary>The configurations' names, the active one among them.</summary>
    public IReadOnlyList<string> ConfigurationNames =>
        Configurations is { Count: > 0 } l ? l.Select(c => c.Name).ToList() : new List<string> { ActiveConfiguration ?? DefaultConfiguration };

    public string ActiveConfigurationName => ActiveConfiguration ?? DefaultConfiguration;

    /// <summary>The components' and mates' state as a configuration named <paramref name="name"/>.</summary>
    public AssemblyConfiguration CaptureConfiguration(string name)
    {
        var c = new AssemblyConfiguration { Name = name };
        foreach (var comp in Components)
        {
            if (comp.Suppressed) (c.Suppressed ??= new()).Add(comp.Id);
            if (comp.Hidden) (c.Hidden ??= new()).Add(comp.Id);
            if (comp.Parameters is { Count: > 0 } p) (c.Parameters ??= new())[comp.Id] = new Dictionary<string, string>(p);
            if (comp.Configuration != null) (c.SubConfigurations ??= new())[comp.Id] = comp.Configuration;
        }
        foreach (var m in Mates) if (m.Suppressed) (c.SuppressedMates ??= new()).Add(m.Id);
        return c;
    }

    /// <summary>The active configuration's record brought up to date from the components (before saving, or switching).</summary>
    public void RecordActiveConfiguration()
    {
        if (Configurations is not { Count: > 0 } l) return;
        var name = ActiveConfigurationName;
        var i = l.FindIndex(c => c.Name == name);
        if (i < 0) l.Add(CaptureConfiguration(name));
        else l[i] = CaptureConfiguration(name);
    }

    /// <summary>
    /// Adds a configuration as the assembly is now and makes it the active one (the first added keeps the state before
    /// as "Default"). False if the name is taken or empty.
    /// </summary>
    public bool AddConfiguration(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || ConfigurationNames.Contains(name)) return false;
        Configurations ??= new List<AssemblyConfiguration> { CaptureConfiguration(ActiveConfigurationName) };
        RecordActiveConfiguration();
        Configurations.Add(CaptureConfiguration(name));
        ActiveConfiguration = name;
        return true;
    }

    /// <summary>
    /// Makes a configuration the active one: the current one's state recorded, the other's put on the components and
    /// mates (displays not loaded here: <see cref="AssemblyModel.ActivateConfiguration"/>). False if there is none so named.
    /// </summary>
    public bool ActivateConfiguration(string name)
    {
        if (name == ActiveConfigurationName) return true;
        if (Configurations?.FirstOrDefault(c => c.Name == name) is not { } target) return false;
        RecordActiveConfiguration();
        foreach (var comp in Components)
        {
            comp.Suppressed = target.Suppressed?.Contains(comp.Id) == true;
            comp.Hidden = target.Hidden?.Contains(comp.Id) == true;
            comp.Parameters = target.Parameters?.GetValueOrDefault(comp.Id) is { } p ? new Dictionary<string, string>(p) : null;
            if (comp.IsAssembly) comp.Configuration = target.SubConfigurations?.GetValueOrDefault(comp.Id);
        }
        foreach (var m in Mates) m.Suppressed = target.SuppressedMates?.Contains(m.Id) == true;
        ActiveConfiguration = name;
        return true;
    }

    public bool RenameConfiguration(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || ConfigurationNames.Contains(to)) return false;
        if (Configurations?.FirstOrDefault(c => c.Name == from) is { } c) c.Name = to;
        else if (from != ActiveConfigurationName) return false;
        if (ActiveConfigurationName == from) ActiveConfiguration = to;
        return true;
    }

    /// <summary>Deletes a configuration other than the active one; false if it is the active one or there is none so named.</summary>
    public bool DeleteConfiguration(string name)
    {
        if (name == ActiveConfigurationName || Configurations == null) return false;
        if (Configurations.RemoveAll(c => c.Name == name) == 0) return false;
        if (Configurations.Count <= 1) Configurations = null;
        return true;
    }

    /// <summary>
    /// The names component and mate ids go by changed everywhere the document keeps them (configurations, exploded
    /// views, patterns): <paramref name="map"/> gives a path's new name, or null when what it names is gone.
    /// (Mates are the caller's: their references change with their own rules.)
    /// </summary>
    public void RenameIds(Func<string, string?> map)
    {
        List<string>? L(List<string>? l)
        {
            if (l == null) return null;
            var r = l.Select(map).OfType<string>().Distinct().ToList();
            return r.Count > 0 ? r : null;
        }
        Dictionary<string, T>? D<T>(Dictionary<string, T>? d)
        {
            if (d == null) return null;
            var r = new Dictionary<string, T>();
            foreach (var (k, v) in d) if (map(k) is { } nk) r[nk] = v;
            return r.Count > 0 ? r : null;
        }
        foreach (var c in Configurations ?? new())
        {
            c.Suppressed = L(c.Suppressed);
            c.Hidden = L(c.Hidden);
            c.Parameters = D(c.Parameters);
            c.SubConfigurations = D(c.SubConfigurations);
        }
        foreach (var v in Explodes ?? new())
        {
            foreach (var s in v.Steps) s.Components = L(s.Components) ?? new();
            v.Steps.RemoveAll(s => s.Components.Count == 0);
        }
        foreach (var p in Patterns ?? new())
        {
            p.Seeds = L(p.Seeds) ?? new();
            // (its direction or plane on a component gone: the assembly's Front plane instead)
            if (p.Reference.Component is { } rc)
            {
                if (map(rc) is { } nrc) p.Reference.Component = nrc;
                else p.Reference = new MateRef { Face = "Front", Kind = MateGeomKind.Plane, D = Kernel.Vec3.UnitZ };
            }
            foreach (var i in p.Instances) i.Seed = map(i.Seed) ?? "";
            p.Instances.RemoveAll(i => i.Seed == "" || map(i.Component) == null);
            foreach (var i in p.Instances) i.Component = map(i.Component)!;
        }
        Patterns?.RemoveAll(p => p.Seeds.Count == 0);
    }
}
