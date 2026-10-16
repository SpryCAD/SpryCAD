using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>Where a part's display came from.</summary>
public enum PartSource { File, Embedded, Cache, None }

/// <summary>
/// One part as an assembly shows it, as configured for the components showing it: all its bodies as one display
/// mesh, in its color.
/// </summary>
public sealed class PartDisplay
{
    public required DisplayMesh Mesh { get; init; }
    /// <summary>How it looks: its part's appearance (own, else its material's), or a cache's.</summary>
    public required Appearance Appearance { get; init; }
    public double[] Color => Appearance.Color;
    public required PartSource Source { get; init; }
    /// <summary>Its cosmetic threads, in the part's coordinates.</summary>
    public IReadOnlyList<CosmeticThread> Threads { get; init; } = Array.Empty<CosmeticThread>();
    /// <summary>The part's revision (of its file or embedded copy; of the cache when shown from it).</summary>
    public string? Revision { get; init; }
    /// <summary>The document built when loaded (not when shown from the cache): the part as configured.</summary>
    public CadDocument? Doc { get; init; }
    /// <summary>The part's own document when loaded (its parameters' own values): <see cref="Doc"/> unless configured.</summary>
    public CadDocument? Part { get; init; }
    /// <summary>The configuration shown (<see cref="Configurations.KeyOf"/>): "" for the part as it is.</summary>
    public string Config { get; init; } = "";
    /// <summary>Why the part could not be shown as it is (missing, could not be read or rebuilt), if so.</summary>
    public string? Problem { get; init; }
    /// <summary>Shown from a cache as good as a rebuild (this revision and configuration, made by this code).</summary>
    public bool Cached { get; init; }
    /// <summary>Waiting to be built (<see cref="AssemblyModel.Pending"/>): shown meanwhile as it last was, or not at all.</summary>
    public bool Building { get; init; }
}

/// <summary>
/// An open assembly: its document, where its parts were found (<see cref="ComponentResolver"/>) and each part's
/// display — built from the part's file or embedded copy, or taken from the assembly's cache when the part is
/// missing or all its components are lightweight. A part configured differently by some of its components
/// (<see cref="Component.Parameters"/>) has a display per configuration. <see cref="RefreshCaches"/> keeps the caches
/// of the revisions in use (called before saving), so the assembly opens and shares without its parts.
/// Components that are assemblies (sub-assemblies) are opened as models of their own (<see cref="Subs"/>).
/// </summary>
public sealed partial class AssemblyModel
{
    public AssemblyDocument Doc { get; }
    public string? Path { get; private set; }
    public PartIndex? Index { get; }
    public ComponentResolver.ResolveResult Resolution { get; private set; } = new();
    /// <summary>By part and configuration (<see cref="DisplayKey"/>; the part's key when it is not configured).</summary>
    public Dictionary<string, PartDisplay> Parts { get; } = new();
    /// <summary>Lines for the user: parts found elsewhere, missing, given an identity.</summary>
    public List<string> Notes { get; } = new();

    AssemblyModel(AssemblyDocument doc, string? path, PartIndex? index, IEnumerable<string>? searchRoots = null, AssemblyModel? parent = null)
    {
        Doc = doc; Path = path; Index = index; Parent = parent;
        _searchRoots = searchRoots?.ToList();
    }

    /// <summary>
    /// Opens an assembly: finds its parts and takes each one's display from the cache when that is current, else builds
    /// it (with <paramref name="defer"/>: later, <see cref="PendingJobs"/>).
    /// </summary>
    public static AssemblyModel Open(AssemblyDocument doc, string? path, PartIndex? index = null, IEnumerable<string>? searchRoots = null, bool defer = false)
        => Open(doc, path, index, searchRoots, defer, null);

    static AssemblyModel Open(AssemblyDocument doc, string? path, PartIndex? index, IEnumerable<string>? searchRoots, bool defer, AssemblyModel? parent)
    {
        var m = new AssemblyModel(doc, path, index, searchRoots, parent);
        if (parent != null) m.Threads = parent.Threads;
        m.Resolution = ComponentResolver.Resolve(doc, path, index, searchRoots);
        m.Notes.AddRange(m.Resolution.Notes);
        foreach (var g in doc.Components.GroupBy(c => ComponentResolver.Key(c.Part)).ToList())
        {
            if (g.First().IsAssembly) m.LoadSub(g.Key, defer);
            else m.LoadPart(g.Key, defer);
        }
        if (doc.LegacyIds) doc.RenewLegacyIds(p => p.Contains('/') && m.Find(p) is { Component: { IsAssembly: false, Part.PartId: { } id } } ? id : null);
        return m;
    }

    public static AssemblyModel New() => new(new AssemblyDocument(), null, null);

    /// <summary>
    /// How its parts' threads are shown (View > Threads), its sub-assemblies' too: as each thread is set (as the
    /// caches have them), or all modeled or all cosmetic (<see cref="SetThreads"/>).
    /// </summary>
    public ThreadDisplay Threads { get; private set; }

    /// <summary>
    /// Shows the threads of its parts and its sub-assemblies' another way: their displays built again (later:
    /// <see cref="PendingJobs"/>; lightweight ones kept as their caches show them). Mates are not solved here.
    /// </summary>
    public void SetThreads(ThreadDisplay mode)
    {
        if (Threads == mode) return;
        Threads = mode;
        foreach (var key in Doc.Components.Where(c => !c.IsAssembly).Select(c => ComponentResolver.Key(c.Part)).Distinct().ToList()) LoadPart(key, defer: true);
        foreach (var sub in Subs.Values) sub.Model?.SetThreads(mode);
    }

    /// <summary>
    /// Which display a component shows: its part's key (<see cref="ComponentResolver.Key"/>), and its configuration
    /// when it has one ("key|size=m4;length=12").
    /// </summary>
    public static string DisplayKey(Component c)
    {
        // (a pinned one: the revision or tag pinned to, before its configuration)
        var config = (c.Part.Pin != null || c.Part.PinTag != null ? "@" + (c.Part.PinTag != null ? "tag:" + c.Part.PinTag : c.Part.Pin) + ";" : "")
            + Configurations.KeyOf(c.Parameters);
        return ComponentResolver.Key(c.Part) + (config.Length > 0 ? "|" + config : "");
    }

    /// <summary>Whether a display key is one of a part's (the part as it is, or configured).</summary>
    static bool OfPart(string displayKey, string partKey) => displayKey == partKey || displayKey.StartsWith(partKey + "|", StringComparison.Ordinal);

    public PartDisplay? DisplayOf(Component c) => Parts.GetValueOrDefault(DisplayKey(c));

    /// <summary>The components of a part (however configured), or of a sub-assembly.</summary>
    public IEnumerable<Component> ComponentsOf(string key) => Doc.Components.Where(c => ComponentResolver.Key(c.Part) == key);

    /// <summary>The components showing a display (<see cref="DisplayKey"/>).</summary>
    public IEnumerable<Component> ComponentsShowing(string displayKey) => Doc.Components.Where(c => DisplayKey(c) == displayKey);

    /// <summary>
    /// (Re)builds a part's displays, one per configuration its components use, and drops those no component uses
    /// any more. Each display comes
    /// <list type="bullet">
    /// <item>from the assembly's cache when that is of the part's revision and configuration and was made by this
    /// code (<see cref="GeometryVersion"/>): what a rebuild would give, so the part is only read, not rebuilt;</item>
    /// <item>from the cache it last showed when its components are lightweight, or the part is missing or cannot
    /// be read;</item>
    /// <item>else built from the part: now, or with <paramref name="defer"/> later (<see cref="Pending"/>, built by
    /// <see cref="PendingJobs"/>), meanwhile shown from an older cache if there is one, else not at all.</item>
    /// </list>
    /// </summary>
    public void LoadPart(string key, bool defer = false)
    {
        var comps = ComponentsOf(key).Where(c => !c.IsAssembly).ToList();
        foreach (var k in Parts.Keys.Where(k => OfPart(k, key) && !comps.Any(c => DisplayKey(c) == k)).ToList()) Parts.Remove(k);
        Pending.RemoveWhere(k => OfPart(k, key));
        if (comps.Count == 0) return;
        var r = comps[0].Part;
        var groups = comps.GroupBy(DisplayKey).ToList();
        // the part read once for each revision shown (the latest, each one pinned), when one is needed (not for a
        // lightweight component with its cache)
        var read = new Dictionary<string, (CadDocument? doc, PartSource src, string? problem)>();
        (CadDocument? doc, PartSource src, string? problem) Read(PartRef pr)
        {
            var which = pr.PinTag != null ? "tag:" + pr.PinTag : pr.Pin ?? "";
            if (read.TryGetValue(which, out var had)) return had;
            CadDocument? d = null; var s = PartSource.File; string? p = null;
            try
            {
                d = ReadPart(key, out s, pr);
                if (d == null) p = "not found";
            }
            catch (Exception ex) { p = ex.Message; }
            return read[which] = (d, s, p);
        }
        foreach (var g in groups)
        {
            var first = g.First();
            var config = Configurations.KeyOf(first.Parameters);
            bool light = g.All(c => c.Lightweight) && CachedOf(first) != null;
            var (doc, src, problem) = light ? (null, PartSource.File, null) : Read(first.Part);
            string? why = problem;
            if (!light && doc != null)
            {
                var built = doc.Configured(first.Parameters);
                // a cache of this very revision and configuration, made by this code: as good as a rebuild
                if (Threads == ThreadDisplay.AsSet && doc.Id != null && CacheOf(doc.Id, doc.Revision, config) is { } current && MeshCache.VersionOf(current) == GeometryVersion.Current)
                {
                    try
                    {
                        var e = MeshCache.Read(current);
                        Parts[g.Key] = new PartDisplay { Mesh = e.Mesh, Threads = e.Threads, Appearance = doc.EffectiveAppearance, Source = src, Revision = doc.Revision, Doc = built, Part = doc, Config = config, Cached = true };
                        continue;
                    }
                    catch { } // (a damaged cache: rebuilt)
                }
                if (defer)
                {
                    // shown as it last was until built (or not at all)
                    var old = CachedOf(first) is { } oc ? MeshCache.Read(oc) : null;
                    Parts[g.Key] = new PartDisplay
                    {
                        Mesh = old?.Mesh ?? new DisplayMesh(), Threads = old?.Threads ?? Array.Empty<CosmeticThread>(), Appearance = doc.EffectiveAppearance, Source = src,
                        Revision = r.Revision, Doc = built, Part = doc, Config = config, Building = true,
                    };
                    Pending.Add(g.Key);
                    continue;
                }
                try
                {
                    Parts[g.Key] = BuildDisplay(doc, built, src, config, Threads);
                    continue;
                }
                catch (Exception ex) { why = ex.Message; }
            }
            // from the cache (lightweight, missing, or it could not be read)
            if (CachedOf(first) is { } cache)
            {
                var e = MeshCache.Read(cache);
                Parts[g.Key] = new PartDisplay { Mesh = e.Mesh, Threads = e.Threads, Appearance = e.Appearance, Source = PartSource.Cache, Revision = r.Revision, Part = doc, Config = config, Problem = why };
                continue;
            }
            Parts[g.Key] = new PartDisplay { Mesh = new DisplayMesh(), Appearance = Model.Appearance.Default, Source = PartSource.None, Part = doc, Config = config, Problem = why ?? "not found" };
        }
    }

    /// <summary>A display built from a part (<paramref name="built"/>: the part as configured), its threads shown as <paramref name="mode"/> says.</summary>
    static PartDisplay BuildDisplay(CadDocument part, CadDocument built, PartSource src, string config, ThreadDisplay mode)
    {
        var (mesh, threads) = Build(built, mode);
        return new PartDisplay { Mesh = mesh, Threads = threads, Appearance = part.EffectiveAppearance, Source = src, Revision = part.Revision, Doc = built, Part = part, Config = config };
    }

    /// <summary>Displays waiting to be built (<see cref="LoadPart"/> with defer), by <see cref="DisplayKey"/>.</summary>
    public HashSet<string> Pending { get; } = new();

    /// <summary>A display to build in the background: <see cref="Build"/> off the UI thread, then <see cref="Install"/> on it.</summary>
    public sealed class PendingJob
    {
        public required string Key { get; init; }
        /// <summary>The display shown meanwhile (the job is stale when it has been replaced).</summary>
        public required PartDisplay Placeholder { get; init; }
        /// <summary>Its name, for progress ("Socket screw (M8, L 30)").</summary>
        public required string Name { get; init; }
        /// <summary>The assembly whose display it is (this one, or a sub-assembly's).</summary>
        public AssemblyModel? Owner { get; init; }
        /// <summary>How its threads are shown (the assembly's <see cref="Threads"/> when the job was made).</summary>
        public ThreadDisplay Threads { get; init; }
        public PartDisplay Build() => BuildDisplay(Placeholder.Part!, Placeholder.Doc!, Placeholder.Source, Placeholder.Config, Threads);
    }

    /// <summary>
    /// The displays waiting to be built, as jobs (in the order the components are listed), the sub-assemblies' after
    /// this assembly's own.
    /// </summary>
    public List<PendingJob> PendingJobs()
    {
        var jobs = new List<PendingJob>();
        foreach (var c in Doc.Components)
        {
            var key = DisplayKey(c);
            if (!Pending.Contains(key) || jobs.Any(j => j.Key == key) || Parts.GetValueOrDefault(key) is not { Doc: not null, Part: not null } pd) continue;
            var config = ConfigLabel(c);
            jobs.Add(new PendingJob { Key = key, Placeholder = pd, Name = c.Name.Split('<')[0] + (config.Length > 0 ? $" ({config})" : ""), Owner = this, Threads = Threads });
        }
        foreach (var sub in Subs.Values)
            if (sub.Model != null) jobs.AddRange(sub.Model.PendingJobs().Where(j => !jobs.Any(x => x.Owner == j.Owner && x.Key == j.Key)));
        return jobs;
    }

    /// <summary>
    /// Shows a built display (or why it could not be built) in place of its placeholder; false when the job is stale
    /// (the part loaded again, configured otherwise or removed meanwhile).
    /// </summary>
    public bool Install(PendingJob job, PartDisplay? built, string? problem = null)
    {
        if (job.Owner != null && job.Owner != this)
        {
            // a sub-assembly's part: shown there, and its mates solved again (its part may have changed); stale when
            // the sub-assembly has been opened again since (its file changed)
            if (!Holds(job.Owner) || !job.Owner.Install(job, built, problem)) return false;
            if (job.Owner.HasMates) job.Owner.SolveMates();
            return true;
        }
        if (!Pending.Contains(job.Key) || !ReferenceEquals(Parts.GetValueOrDefault(job.Key), job.Placeholder)) return false;
        Pending.Remove(job.Key);
        var p = job.Placeholder;
        Parts[job.Key] = built ?? new PartDisplay
        {
            Mesh = p.Mesh, Threads = p.Threads, Appearance = p.Appearance, Source = p.Mesh.Indices.Length > 0 ? PartSource.Cache : PartSource.None,
            Revision = p.Revision, Part = p.Part, Config = p.Config, Problem = problem ?? "could not be built",
        };
        return true;
    }

    /// <summary>A part's document from its embedded copy or its file (null: not found).</summary>
    CadDocument? ReadPart(string key, out PartSource source, PartRef? pinned = null)
    {
        source = PartSource.File;
        if (ComponentsOf(key).FirstOrDefault()?.Part is not { } r) return null;
        if (r.Embedded != null && Doc.Files.TryGetValue(r.Embedded, out var bytes))
        {
            source = PartSource.Embedded;
            return pinned is { } p && (p.Pin != null || p.PinTag != null) ? PinnedPart.Load(bytes, p) : DocumentContainer.Load(bytes, r.Embedded);
        }
        if (Resolution.Parts.GetValueOrDefault(key) is { Status: PartStatus.Found or PartStatus.Moved, Path: { } file })
            return pinned is { } p2 && (p2.Pin != null || p2.PinTag != null) ? PinnedPart.Load(file, p2) : CadDocument.Load(file);
        return null;
    }

    /// <summary>
    /// The part document of a component as saved (its parameters' own values): from a display of its part when
    /// loaded, else read from its file or embedded copy (lightweight components). Null when the part cannot be found.
    /// </summary>
    public CadDocument? PartDocument(Component c)
    {
        var key = ComponentResolver.Key(c.Part);
        if (DisplayOf(c)?.Part is { } d) return d;
        foreach (var (k, pd) in Parts) if (OfPart(k, key) && pd.Part != null) return pd.Part;
        try { return ReadPart(key, out _); } catch { return null; }
    }

    /// <summary>
    /// Configures components (a part's): each given the values that differ from its part's own (none: the part as
    /// it is), and their displays built. Mates are not solved here.
    /// </summary>
    public void Configure(IEnumerable<Component> comps, CadDocument part, IReadOnlyDictionary<string, string>? values)
    {
        var keys = new HashSet<string>();
        foreach (var c in comps)
        {
            c.Parameters = part.Normalize(values);
            keys.Add(ComponentResolver.Key(c.Part));
        }
        foreach (var k in keys) LoadPart(k);
    }

    /// <summary>
    /// A component's configuration as text ("M4, Length 12"): empty when its part has no user-facing parameters; the
    /// values it sets when its part is not loaded (never read for this).
    /// </summary>
    public string ConfigLabel(Component c)
    {
        var key = ComponentResolver.Key(c.Part);
        var part = DisplayOf(c)?.Part ?? Parts.Where(kv => OfPart(kv.Key, key)).Select(kv => kv.Value.Part).FirstOrDefault(p => p != null);
        if (part == null) return c.Parameters == null ? "" : string.Join(", ", c.Parameters.Values);
        return part.UserParameters().Count > 0 ? part.Label(c.Parameters) : "";
    }

    byte[]? CachedOf(Component c) =>
        c.Part.PartId != null && Doc.Caches.TryGetValue(AssemblyDocument.CacheKey(c.Part.PartId, c.Part.Revision, Configurations.KeyOf(c.Parameters)), out var name)
        && Doc.Files.TryGetValue(name, out var data) ? data : null;

    byte[]? CacheOf(string partId, string? revision, string config) =>
        Doc.Caches.TryGetValue(AssemblyDocument.CacheKey(partId, revision, config), out var name) && Doc.Files.TryGetValue(name, out var data) ? data : null;

    /// <summary>
    /// A part's display: its rebuilt bodies as one mesh, and its cosmetic threads (drawn as helices); its threads
    /// shown as each is set, or as <paramref name="threads"/> says.
    /// </summary>
    public static (DisplayMesh mesh, List<CosmeticThread> threads) Build(CadDocument doc, ThreadDisplay threads = ThreadDisplay.AsSet)
    {
        var r = new Regenerator { ThreadOverride = threads }.Rebuild(doc);
        return (Merge(r.State.Bodies.Select(b => b.Mesh)), r.State.Cosmetics.Values.ToList());
    }

    /// <summary>Display meshes as one (triangles, faces and edges renumbered).</summary>
    public static DisplayMesh Merge(IEnumerable<DisplayMesh> meshes)
    {
        var list = meshes.ToList();
        if (list.Count == 1) return list[0];
        var m = new DisplayMesh();
        var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<int>(); var lines = new List<float>();
        foreach (var x in list)
        {
            int vOff = pos.Count / 3, iOff = idx.Count, eOff = lines.Count / 3, edgeOff = m.Edges.Count;
            pos.AddRange(x.Positions); nrm.AddRange(x.Normals);
            foreach (var i in x.Indices) idx.Add(i + vOff);
            foreach (var f in x.Faces) m.Faces.Add(f with { IndexStart = f.IndexStart + iOff });
            lines.AddRange(x.EdgeLines);
            foreach (var e in x.Edges) m.Edges.Add(new EdgeRange(e.EdgeIndex + edgeOff, e.VertexStart + eOff, e.VertexCount));
            m.Bounds.Add(x.Bounds);
            m.Volume += x.Volume;
        }
        m.Positions = pos.ToArray(); m.Normals = nrm.ToArray(); m.Indices = idx.ToArray(); m.EdgeLines = lines.ToArray();
        return m;
    }

    /// <summary>
    /// Adds a component of the part at <paramref name="partPath"/>, linked to the file or embedded in the assembly (a
    /// copy: the assembly is then self-contained). A part without an identity (saved before them) is given one: its
    /// file is saved again (a note says so).
    /// </summary>
    public Component Insert(string partPath, Transform placement, bool embed = false)
    {
        if (IsAssemblyFile(partPath)) return InsertAssembly(partPath, placement);
        partPath = System.IO.Path.GetFullPath(partPath);
        var doc = CadDocument.Load(partPath);
        if (doc.Id == null)
        {
            doc.Save(partPath);
            Notes.Add($"{System.IO.Path.GetFileName(partPath)} was given an identity (saved again) so the assembly can always find it.");
        }
        Index?.Record(doc.Id!, doc.Revision, partPath);
        var r = new PartRef { PartId = doc.Id, Revision = doc.Revision, AbsolutePath = partPath };
        var dir = Path != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) : null;
        r.Path = dir != null ? ComponentResolver.Relative(dir, partPath) : System.IO.Path.GetFileName(partPath);
        if (embed)
        {
            // one copy per part, shared by its components
            var existing = Doc.Components.Select(c => c.Part).FirstOrDefault(p => p.Embedded != null && p.PartId == doc.Id);
            r.Embedded = existing?.Embedded ?? UniqueName("parts/" + System.IO.Path.GetFileName(partPath));
            if (existing == null) Doc.Files[r.Embedded] = File.ReadAllBytes(partPath);
        }
        else
        {
            // linked: the same reference as the part's other linked components
            if (Doc.Components.Select(c => c.Part).FirstOrDefault(p => p.Embedded == null && p.PartId == doc.Id) is { } same) r = same.Clone();
        }
        var c = Doc.AddComponent(r, placement, System.IO.Path.GetFileNameWithoutExtension(partPath));
        var key = ComponentResolver.Key(r);
        if (!Resolution.Parts.ContainsKey(key))
            Resolution.Parts[key] = new PartResolution(embed ? PartStatus.Embedded : PartStatus.Found, embed ? null : partPath, false, null);
        if (!Parts.ContainsKey(key))
        {
            var (mesh, threads) = Build(doc, Threads);
            Parts[key] = new PartDisplay { Mesh = mesh, Threads = threads, Appearance = doc.EffectiveAppearance, Source = embed ? PartSource.Embedded : PartSource.File, Revision = doc.Revision, Doc = doc, Part = doc };
        }
        return c;
    }

    /// <summary>
    /// Adds a component of a part document embedded in the assembly (the document saved into it once per part:
    /// further components of it share the copy). The document gets an identity if it has none.
    /// </summary>
    public Component InsertEmbedded(CadDocument doc, string name, Transform placement) => InsertEmbedded(doc, name, placement, null);

    /// <summary>
    /// <see cref="InsertEmbedded(CadDocument, string, Transform)"/> with the part already saved (<see cref="SaveEmbedded"/>)
    /// and its display built, as several parts are made at once (<see cref="FromStep"/>).
    /// </summary>
    Component InsertEmbedded(CadDocument doc, string name, Transform placement, (byte[] file, DisplayMesh? mesh, List<CosmeticThread>? threads)? made)
    {
        var existing = doc.Id != null ? Doc.Components.Select(c => c.Part).FirstOrDefault(p => p.Embedded != null && p.PartId == doc.Id) : null;
        PartRef r;
        if (existing != null) r = existing.Clone();
        else
        {
            // saved as a part file would be (its identity and revision given), kept inside the assembly
            Doc.Files[UniqueName("parts/" + SafeFileName(name) + CadDocument.FileExtension)] = made?.file ?? SaveEmbedded(doc);
            var file = Doc.Files.Keys.Last(k => k.StartsWith("parts/" + SafeFileName(name), StringComparison.Ordinal));
            r = new PartRef { PartId = doc.Id, Revision = doc.Revision, Embedded = file };
        }
        var c = Doc.AddComponent(r, placement, name);
        var key = ComponentResolver.Key(r);
        if (!Resolution.Parts.ContainsKey(key)) Resolution.Parts[key] = new PartResolution(PartStatus.Embedded, null, false, null);
        if (!Parts.ContainsKey(key))
        {
            if (made is { mesh: null })
            {
                // its display left to build (PendingJobs): shown empty until then
                Parts[key] = new PartDisplay { Mesh = new DisplayMesh(), Threads = Array.Empty<CosmeticThread>(), Appearance = doc.EffectiveAppearance, Source = PartSource.Embedded, Revision = doc.Revision, Doc = doc, Part = doc, Building = true };
                Pending.Add(key);
                return c;
            }
            var (mesh, threads) = made is { mesh: { } mm } m ? (mm, m.threads!) : Build(doc, Threads);
            Parts[key] = new PartDisplay { Mesh = mesh, Threads = threads, Appearance = doc.EffectiveAppearance, Source = PartSource.Embedded, Revision = doc.Revision, Doc = doc, Part = doc };
        }
        return c;
    }

    /// <summary>A part saved as a part file is (its identity and revision given), as the bytes an assembly embeds.</summary>
    static byte[] SaveEmbedded(CadDocument doc)
    {
        // (made in memory: no file written and read back)
        return DocumentContainer.ToNewBytes(doc, "Part");
    }

    static string SafeFileName(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        var s = new string(name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return s.Length == 0 ? "Part" : s;
    }

    /// <summary>
    /// An assembly from a STEP assembly file: each of its parts a part document embedded in the assembly (an Import
    /// of that part's solids, in its own coordinates), each use of a part a component where the file places it, its
    /// sub-assemblies embedded sub-assemblies. Every component is fixed there, so the assembly stays as it was
    /// designed until one is floated (then mate it as wanted). Null when the file is not an assembly (or holds one part, once).
    /// With <paramref name="defer"/> the parts' displays are left to build (<see cref="PendingJobs"/>): the assembly
    /// shows at once and its parts appear as each is built.
    /// </summary>
    public static AssemblyModel? FromStep(byte[] data, string name, bool defer = false)
    {
        StepAssemblyResult? res; using (Kernel.Prof.Time("stepasm.read")) res = StepImport.ReadAssembly(data, "S.");
        // no assembly structure, or a single use of a single part (a part, as STEP parts are opened)
        if (res == null || res.Occurrences.Count < 2) return null;
        var m = New();
        m.Notes.AddRange(res.Warnings);
        // a part document per STEP part: an Import of its solids, kept as the assembly file was read (SpryCAD's own file
        // of exact solids: not written to a STEP of their own and read again); a part holding something those files do
        // not, as a STEP of its own
        var names = new string[res.Parts.Count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < res.Parts.Count; i++)
        {
            var n = res.Parts[i].Name;
            for (int k = 2; !used.Add(n); k++) n = $"{res.Parts[i].Name} ({k})";
            names[i] = n;
        }
        // each part's document written, saved (as embedded) and its display built: the parts are independent, so at
        // once (most of opening the file; reading it is a fraction), then put in the assembly in order as before
        var docs = new CadDocument[res.Parts.Count];
        var made = new (byte[] file, DisplayMesh? mesh, List<CosmeticThread>? threads)[res.Parts.Count];
        var usedParts = res.Occurrences.Select(o => o.Part).ToHashSet();
        var threadsMode = m.Threads;
        using (Kernel.Prof.Time("stepasm.parts"))
        Parallel.For(0, res.Parts.Count, i =>
        {
            byte[] bytes; string ext;
            // its color in the file (its first colored solid's) as its appearance
            var color = res.Parts[i].Solids.Select(sd => sd.Color).FirstOrDefault(c => c != null);
            try { bytes = NBrepFile.Write(res.Parts[i].Solids, "S."); ext = ".sbrep"; }
            catch (NotSupportedException)
            {
                using var ms = new MemoryStream();
                StepWriter.Write(ms, res.Parts[i].Solids.Select(sd => (sd.Name, Kernel.Native.NTess.ToSolid(sd.Body))), names[i], color: color);
                bytes = ms.ToArray(); ext = ".step";
            }
            var doc = new CadDocument { Appearance = color != null ? Appearance.FromRgba(color) : null };
            var file = "imports/" + SafeFileName(names[i]) + ext;
            doc.AddFile(file, bytes);
            doc.AddFeature(new ImportFeature { File = file, Sha256 = DocumentContainer.Hash(bytes) });
            docs[i] = doc;
            if (!usedParts.Contains(i)) return;
            var saved = SaveEmbedded(doc);
            if (defer) { made[i] = (saved, null, null); return; }
            var (mesh, threads) = Build(doc, threadsMode);
            made[i] = (saved, mesh, threads);
        });
        var pIns = Kernel.Prof.Time("stepasm.insert");
        // its sub-assemblies as the file has them (one top product holding others): each an embedded sub-assembly,
        // rigid, made once however often it is used, its components fixed where the file puts them; else every use
        // of a part a component of the assembly itself
        var subs = new Dictionary<int, (AssemblyModel model, int height)>();
        var placed = new List<(AssemblyModel owner, string file, int product)>(); // where each sub-assembly's file goes
        (AssemblyModel model, int height) Sub(int p)
        {
            if (subs.TryGetValue(p, out var had)) return had;
            var sm = New();
            sm.Doc.Id = CadDocument.NewGuid();
            int height = 0;
            foreach (var u in res.Products[p].Uses)
            {
                if (u.Part >= 0) { sm.InsertEmbedded(docs[u.Part], names[u.Part], u.Placement, made[u.Part]).Fixed = true; continue; }
                var (child, h) = Sub(u.Product);
                height = Math.Max(height, h + 1);
                sm.InsertEmbeddedSub(child, u.Product, res.Products[u.Product].Name, u.Placement, placed).Fixed = true;
            }
            return subs[p] = (sm, height);
        }
        var top = res.Roots.Count == 1 && res.Products[res.Roots[0]].Uses.Any(u => u.Product >= 0)
            ? res.Products[res.Roots[0]].Uses
            : res.Occurrences.Select(o => new StepUse(o.Part, -1, o.Name, o.Placement)).ToList();
        // each fixed where the file puts it (float one to move it, then mate it as wanted)
        foreach (var u in top)
            (u.Part >= 0 ? m.InsertEmbedded(docs[u.Part], names[u.Part], u.Placement, made[u.Part]) : m.InsertEmbeddedSub(Sub(u.Product).model, u.Product, res.Products[u.Product].Name, u.Placement, placed)).Fixed = true;
        // the sub-assemblies' files: each holds those of the sub-assemblies it uses, so the innermost first; those as
        // deep as each other at once (a file read back at once: not synced to disk)
        using (Kernel.Prof.Time("stepasm.subfiles"))
            foreach (var level in subs.GroupBy(kv => kv.Value.height).OrderBy(g => g.Key))
            {
                var files = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();
                Parallel.ForEach(level, kv => files[kv.Key] = AssemblyContainer.ToBytes(kv.Value.model.Doc, durable: false));
                foreach (var (owner, file, product) in placed.Where(x => files.ContainsKey(x.product)))
                {
                    owner.Doc.Files[file] = files[product];
                    var revision = subs[product].model.Doc.Revision;
                    foreach (var c in owner.Doc.Components.Where(c => c.Part.Embedded == file)) c.Part.Revision = revision;
                }
            }
        if (m.HasMates) m.SolveMates();
        pIns.Dispose();
        return m;
    }

    /// <summary>
    /// Adds a component of a sub-assembly made here (<see cref="FromStep"/>), shown by that model: its file embedded
    /// once (further components of it share the copy), written later (where noted in <paramref name="placed"/>). Rigid.
    /// </summary>
    Component InsertEmbeddedSub(AssemblyModel sub, int product, string name, Transform placement, List<(AssemblyModel owner, string file, int product)> placed)
    {
        var sd = sub.Doc;
        PartRef r;
        if (Doc.Components.Select(c => c.Part).FirstOrDefault(p => p.Assembly && p.Embedded != null && p.PartId == sd.Id) is { } same) r = same.Clone();
        else
        {
            r = new PartRef { PartId = sd.Id, Assembly = true, Embedded = UniqueName("subs/" + SafeFileName(name) + AssemblyDocument.FileExtension) };
            Doc.Files[r.Embedded] = Array.Empty<byte>();
            placed.Add((this, r.Embedded, product));
        }
        var key = ComponentResolver.Key(r);
        if (!Resolution.Parts.ContainsKey(key)) Resolution.Parts[key] = new PartResolution(PartStatus.Embedded, null, false, null);
        var c = Doc.AddComponent(r, placement, name);
        if (!Subs.ContainsKey(SubKey(c))) Subs[SubKey(c)] = new SubAssembly { Model = sub, Source = SubSource.Embedded };
        return c;
    }

    string UniqueName(string name)
    {
        if (!Doc.Files.ContainsKey(name)) return name;
        var ext = System.IO.Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (int i = 2; ; i++) if (!Doc.Files.ContainsKey($"{stem}-{i}{ext}")) return $"{stem}-{i}{ext}";
    }

    /// <summary>Removes a component (its mates, and an embedded part no other component uses, go with it).</summary>
    public void Remove(Component c)
    {
        // a pattern's seed: its copies go with it
        foreach (var copy in (Doc.Patterns ?? new()).SelectMany(p => p.Instances).Where(i => i.Seed == c.Id).Select(i => Doc.Find(i.Component)).OfType<Component>().ToList())
            Remove(copy);
        Doc.Components.Remove(c);
        bool Of(MateRef r) => r.Component != null && (r.Component == c.Id || r.Component.StartsWith(c.Id + "/", StringComparison.Ordinal));
        Doc.Mates.RemoveAll(m => Of(m.A) || Of(m.B));
        var key = ComponentResolver.Key(c.Part);
        Doc.RenameIds(p => p == c.Id || p.StartsWith(c.Id + "/", StringComparison.Ordinal) ? null : p);
        // copies of a pattern that is gone are components of their own
        foreach (var x in Doc.Components.Where(x => x.Pattern != null && Doc.Patterns?.Any(p => p.Id == x.Pattern) != true)) x.Pattern = null;
        if (Doc.Patterns is { Count: 0 }) Doc.Patterns = null;
        if (c.IsAssembly)
        {
            if (!ComponentsOf(key).Any() && c.Part.Embedded != null) Doc.Files.Remove(c.Part.Embedded);
            foreach (var k in Subs.Keys.Where(k => OfPart(k, key) && !Doc.Components.Any(x => x.IsAssembly && SubKey(x) == k)).ToList()) Subs.Remove(k);
            return;
        }
        var shown = DisplayKey(c);
        if (!ComponentsShowing(shown).Any()) Parts.Remove(shown);
        if (!ComponentsOf(key).Any() && c.Part.Embedded != null) Doc.Files.Remove(c.Part.Embedded);
    }

    // ------------------------------------------------------------------------------------------
    // mates

    /// <summary>The assembly's origin planes (mate references with no component).</summary>
    public static readonly string[] OriginPlanes = { "Front", "Top", "Right" };

    /// <summary>
    /// A face of a component's part as a mate reference (its part's coordinates); null if it has none. With
    /// <paramref name="end"/>: the center of a round face's circle at that end (<see cref="MateRef.End"/>), a point.
    /// </summary>
    public MateGeom? FaceGeom(string componentId, string face, int? end = null)
    {
        if (DisplayAt(componentId) is not { } pd) return null;
        foreach (var f in pd.Mesh.Faces)
        {
            if (f.Name != face) continue;
            var g = MateGeom.Of(f);
            if (end == null || g is not { Kind: MateGeomKind.Axis }) return end == null ? g : null;
            if (AxisSpan(pd.Mesh, f) is not { } span) return null;
            var t = end > 0 ? span.hi : span.lo;
            // (no radius: a point with one is a sphere's center, which Tangent would mate as the sphere)
            return new MateGeom(MateGeomKind.Point, g.P + g.D * t, g.D);
        }
        return null;
    }

    /// <summary>How far a round face reaches along its axis (from the axis's origin point), from its triangles.</summary>
    public static (double lo, double hi)? AxisSpan(DisplayMesh m, FaceRange f)
    {
        var o = f.Surface.Origin; var d = f.Surface.Axis.Normalized;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int k = f.IndexStart; k < f.IndexStart + f.IndexCount; k++)
        {
            var i = m.Indices[k];
            var t = Vec3.Dot(new Vec3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]) - o, d);
            lo = Math.Min(lo, t); hi = Math.Max(hi, t);
        }
        return lo <= hi ? (lo, hi) : null;
    }

    /// <summary>
    /// A round face's axis where its component is placed, as long as the face (to draw it); null for other faces.
    /// </summary>
    public (Vec3 a, Vec3 b)? AxisSegment(string componentId, string face)
    {
        if (Find(componentId) is not { } o || DisplayAt(componentId) is not { } pd) return null;
        var f = pd.Mesh.Faces.FirstOrDefault(x => x.Name == face);
        if (f == null || MateGeom.Of(f) is not { Kind: MateGeomKind.Axis } g || AxisSpan(pd.Mesh, f) is not { } span) return null;
        var pad = Math.Max(2, (span.hi - span.lo) * 0.15);
        var t = o.World;
        return (t.Point(g.P + g.D * (span.lo - pad)), t.Point(g.P + g.D * (span.hi + pad)));
    }

    /// <summary>The end of a round face (<see cref="MateRef.End"/>) nearer a point in the part's coordinates (a pick).</summary>
    public int? NearerEnd(string componentId, string face, Vec3 worldPoint)
    {
        if (Find(componentId) is not { } o || FaceGeom(componentId, face, 1) is not { } far || FaceGeom(componentId, face, -1) is not { } near) return null;
        var p = o.World.RigidInverse().Point(worldPoint);
        return Vec3.Distance(p, far.P) <= Vec3.Distance(p, near.P) ? 1 : -1;
    }

    /// <summary>
    /// A reference's geometry in its part's coordinates: the face as the part has it now (the reference's copy
    /// updated), else the copy (lightweight or missing part, or the face gone). Origin planes: the assembly's, or (a
    /// reference to a sub-assembly) the sub-assembly's.
    /// </summary>
    public MateGeom Local(MateRef r)
    {
        if (r.Component == null || Find(r.Component) is { Component.IsAssembly: true })
            return r.Face switch
            {
                "Top" => new MateGeom(MateGeomKind.Plane, Vec3.Zero, Vec3.UnitY),
                "Right" => new MateGeom(MateGeomKind.Plane, Vec3.Zero, Vec3.UnitX),
                _ => new MateGeom(MateGeomKind.Plane, Vec3.Zero, Vec3.UnitZ),
            };
        if (FaceGeom(r.Component, r.Face, r.End) is { } g)
        {
            r.Kind = g.Kind; r.P = g.P; r.D = g.D; r.R = g.R;
            return g;
        }
        return r.Geom;
    }

    /// <summary>A reference's geometry where its component is placed.</summary>
    public MateGeom World(MateRef r) => Local(r).Placed(PlacementOf(r));

    /// <summary>
    /// A reference to a face of a component, or with <paramref name="end"/> to the center of a round face's circle there
    /// (its geometry copied); null if it cannot be mated.
    /// </summary>
    public MateRef? RefTo(string? componentId, string face, int? end = null)
    {
        // (a sub-assembly's origin plane, as the assembly's own)
        var plane = componentId == null || Find(componentId) is { Component.IsAssembly: true };
        if (plane && !OriginPlanes.Contains(face)) return null;
        var g = plane ? Local(new MateRef { Component = componentId, Face = face }) : FaceGeom(componentId!, face, end);
        return g == null ? null : new MateRef { Component = componentId, Face = face, End = componentId == null ? null : end, Kind = g.Kind, P = g.P, D = g.D, R = g.R };
    }

    /// <summary>The last solve's result (mate errors).</summary>
    public MateSolver.Result? LastSolve { get; private set; }

    /// <summary>
    /// Moves floating components so the mates hold (<paramref name="onlyMove"/>: just those), flexible sub-assemblies'
    /// mates and components with them.
    /// </summary>
    public MateSolver.Result SolveMates(ISet<string>? onlyMove = null)
    {
        LastSolve = Solve(onlyMove, null);
        // pattern copies follow their seeds (and mates on them are solved with them where they now are)
        if (ApplyPatterns() && Doc.Mates.Any(m => m.A.Component is { } a && IsPatternCopy(a) || m.B.Component is { } b && IsPatternCopy(b)))
            LastSolve = Solve(onlyMove, null);
        return LastSolve;
    }

    bool IsPatternCopy(string path) => Doc.Find(TopOf(path))?.Pattern != null;

    /// <summary>
    /// A component dragged: the mates solved with its grabbed point pulled toward the cursor's ray, from where
    /// everything is (the mates holding); a component no mate moves is moved by the pull's <c>Move</c> instead. A drag
    /// the mates held before and could not be solved to hold after is not taken: everything stays where it was.
    /// Between <see cref="BeginDrag"/> and <see cref="EndDrag"/>, on the drag's solve set up once, to
    /// <see cref="DragTolerance"/>.
    /// </summary>
    public MateSolver.Result SolveDrag(MateSolver.Pull pull)
    {
        var held = LastSolve?.Satisfied == true;
        var before = held ? CapturePoses() : null;
        var d = _drag?.Component == pull.Component ? _drag : null;
        var flatBefore = d?.Flat is { } fb ? FlatState(fb) : null;
        MateSolver.Result res;
        if (d != null)
        {
            res = d.Session.Run(d.Flat?.PullOnBody(pull) ?? pull, DragTolerance);
            if (d.Flat != null) WriteBack(d.Flat, res);
        }
        else res = Solve(null, pull);
        // (one a mate holds still, locked to the base say, stays)
        if (!res.Solved.Contains(pull.Component) && !res.Held.Contains(pull.Component) && (pull.Move != Vec3.Zero || pull.Turn != null))
        {
            SetWorld(pull.Component, (pull.Turn ?? Transform.Translation(pull.Move)) * WorldOf(pull.Component));
            if (d?.Flat?.Doc.Find(pull.Component) is { } fc) fc.Placement = Placement.From(WorldOf(pull.Component));
            res.Moved.Add(pull.Component);
        }
        if (before != null && !res.Satisfied)
        {
            Restore(before);
            if (flatBefore != null) flatBefore();
            return LastSolve!;
        }
        LastSolve = res;
        FollowPatterns(d);
        return LastSolve;
    }

    /// <summary>Pattern copies follow their seeds; a drag's or motion's flattened copy of the assembly with them.</summary>
    void FollowPatterns(DragState? d)
    {
        if (ApplyPatterns() && d?.Flat is { } f)
            foreach (var o in f.Occs)
                if (o.Rel == null) f.Doc.Find(o.Path)!.Placement = o.Component.Placement;
    }

    DragState? _motion;

    /// <summary>The component a motion turns (<see cref="BeginMotion"/>), if one is going on.</summary>
    public string? MotionHeld => _motion?.Component;

    /// <summary>
    /// A motion starts: <paramref name="held"/> is moved by the caller step by step (a motor turns it) and held where
    /// it is put; what is mated to it moves (<see cref="SolveMotion"/>), on a solve set up once for every step; nothing
    /// else may move components until <see cref="EndMotion"/>.
    /// </summary>
    public void BeginMotion(string held)
    {
        var f = Flatten();
        _motion = new DragState(held, f, MateSolver.PrepareHeld(f?.Doc ?? Doc, Local, held, f?.Attached));
    }

    /// <summary>A step of the motion: the mates solved (to <see cref="DragTolerance"/>) with the held component where it is now.</summary>
    public MateSolver.Result SolveMotion()
    {
        if (_motion is not { } d) return SolveMates();
        if (d.Flat?.Doc.Find(d.Component) is { } fc) fc.Placement = Placement.From(WorldOf(d.Component));
        var res = d.Session.Run(null, DragTolerance);
        if (d.Flat != null) WriteBack(d.Flat, res);
        LastSolve = res;
        FollowPatterns(d);
        return res;
    }

    /// <summary>The motion is over: the mates solved exactly, from where it left them.</summary>
    public MateSolver.Result? EndMotion()
    {
        if (_motion is not { } d) return null;
        if (d.Flat?.Doc.Find(d.Component) is { } fc) fc.Placement = Placement.From(WorldOf(d.Component));
        _motion = null;
        var res = d.Session.Run();
        if (d.Flat != null) WriteBack(d.Flat, res);
        LastSolve = res;
        ApplyPatterns();
        return res;
    }

    /// <summary>
    /// How near the mates are held while a component is dragged (mm, or radians for directions): far below what shows,
    /// and well inside <see cref="MateSolver.Result.Satisfied"/>; <see cref="EndDrag"/> solves them exactly.
    /// </summary>
    public const double DragTolerance = 1e-7;

    /// <summary>A drag's solve, set up when it starts (<see cref="BeginDrag"/>).</summary>
    sealed record DragState(string Component, Flat? Flat, MateSolver.Session Session);
    DragState? _drag;

    /// <summary>
    /// A drag of a component starts: its solve is set up once (the assembly flattened, the mates and references looked
    /// up, what moves with it found) for every mouse move of it; nothing but the drag may move components until
    /// <see cref="EndDrag"/>.
    /// </summary>
    public void BeginDrag(string component)
    {
        // whether the mates hold as it starts (not solved since opened: checked, nothing moved), so a drag that would
        // leave them broken is not taken
        LastSolve ??= Solve(new HashSet<string>(), null);
        var f = Flatten();
        var body = f != null && f.Attached.TryGetValue(component, out var at) ? at.Body : component;
        _drag = new DragState(component, f, MateSolver.PrepareDrag(f?.Doc ?? Doc, Local, body, f?.Attached));
    }

    /// <summary>The drag is over: the mates solved exactly, from where it left them.</summary>
    public MateSolver.Result? EndDrag()
    {
        if (_drag is not { } d) return null;
        _drag = null;
        if (LastSolve?.Satisfied != true) return LastSolve;
        var res = d.Session.Run();
        if (d.Flat != null) WriteBack(d.Flat, res);
        LastSolve = res;
        ApplyPatterns();
        return res;
    }

    /// <summary>The flattened assembly's placements and turns as they are, and how to put them back.</summary>
    static Action FlatState(Flat f)
    {
        var placed = f.Doc.Components.Select(c => (c, c.Placement)).ToList();
        var turns = f.Doc.Mates.Select(m => (m, m.AngleA, m.AngleB)).ToList();
        return () =>
        {
            foreach (var (c, p) in placed) c.Placement = p;
            foreach (var (m, a, b) in turns) (m.AngleA, m.AngleB) = (a, b);
        };
    }

    /// <summary>The placement of a reference's component in the assembly (the identity for the assembly's planes).</summary>
    public Transform PlacementOf(MateRef r) => r.Component != null && Find(r.Component) is { } o ? o.World : Transform.Identity;

    /// <summary>
    /// Adds a mate between two references (the alignment and side nearest how they lie now; Lock, Gear and Screw
    /// recording how the components lie), moving the first reference's component onto it if it floats (else the
    /// second's), then solving all the mates. <paramref name="setup"/>: the mate's options (limits, ratio, …) before.
    /// </summary>
    /// <remarks>Without <paramref name="settle"/> nothing is moved or solved (many mates added at once, solved after).</remarks>
    public Mate AddMate(MateType type, MateRef a, MateRef b, double value = 0, Action<Mate>? setup = null, bool settle = true)
    {
        var m = new Mate { Type = type, A = a, B = b, Value = value };
        Mate.Normalize(m);
        m.Capture(World(a), World(b), PlacementOf(a), PlacementOf(b));
        setup?.Invoke(m);
        Doc.AddMate(m);
        if (settle) Settle(a, b);
        return m;
    }

    /// <summary>
    /// A mate edited: made again from its new references, type and values (taken as the components stand now) in
    /// the old one's place, keeping its id, its place in the list and whether it is suppressed; its name too, unless
    /// <paramref name="name"/> gives another. The components move to it as for a new mate.
    /// </summary>
    public Mate ReplaceMate(Mate old, MateType type, MateRef a, MateRef b, double value = 0, Action<Mate>? setup = null, string? name = null)
    {
        var i = Doc.Mates.IndexOf(old);
        if (i < 0) throw new ArgumentException("The mate is not in the assembly.", nameof(old));
        var m = new Mate { Id = old.Id, Name = name ?? old.Name, Type = type, A = a, B = b, Value = value, Suppressed = old.Suppressed };
        Mate.Normalize(m);
        m.Capture(World(a), World(b), PlacementOf(a), PlacementOf(b));
        setup?.Invoke(m);
        Doc.Mates[i] = m;
        Settle(a, b);
        return m;
    }

    // a mate just set up: the component it names first (if it floats) moved onto it, then everything solved
    void Settle(MateRef a, MateRef b)
    {
        var first = Movable(a.Component) ?? Movable(b.Component);
        if (first != null) SolveMates(new HashSet<string> { first });
        SolveMates();
    }

    /// <summary>
    /// Whether assemblies keep their parts' meshes when saved (<see cref="RefreshCaches"/>), so they open without
    /// rebuilding their parts and show parts that have gone missing. Off, only lightweight components' parts keep
    /// theirs (they show from them): the files are much smaller, and the parts are rebuilt when the assembly opens.
    /// </summary>
    public static bool KeepMeshes { get; set; } = true;

    /// <summary>
    /// The caches for the parts as shown now: each part shown from its file or embedded copy has its revision's
    /// mesh cached (and its components the revision noted), made again when an older version of the code made it; caches
    /// no component uses are dropped. Displays still waiting to be built keep the cache they show. Displays with their
    /// threads shown otherwise than as set (<see cref="Threads"/>) are not cached: the caches they had are kept. Without
    /// <see cref="KeepMeshes"/> only lightweight components' parts are cached, here and in the embedded sub-assemblies.
    /// </summary>
    public void RefreshCaches()
    {
        var drop = new HashSet<string>();
        foreach (var (key, pd) in Parts)
        {
            if (pd.Building || pd.Source is not (PartSource.File or PartSource.Embedded) || pd.Doc?.Id is not { } id) continue;
            var ck = AssemblyDocument.CacheKey(id, pd.Revision, pd.Config);
            if (!KeepMeshes && !ComponentsShowing(key).Any(c => c.Lightweight))
            {
                foreach (var c in ComponentsShowing(key)) c.Part.Revision = pd.Revision;
                drop.Add(ck);
                continue;
            }
            if (Threads != ThreadDisplay.AsSet)
            {
                foreach (var c in ComponentsShowing(key)) c.Part.Revision = pd.Revision;
                continue;
            }
            var stale = Doc.Caches.TryGetValue(ck, out var had) && (!Doc.Files.TryGetValue(had, out var bytes) || MeshCache.VersionOf(bytes) != GeometryVersion.Current);
            if (!Doc.Caches.ContainsKey(ck) || stale)
            {
                // (a configuration's: named by a hash of it)
                var config = pd.Config.Length > 0 ? "-" + DocumentContainer.Hash(System.Text.Encoding.UTF8.GetBytes(pd.Config))[..12] : "";
                var name = $"cache/{id}-{pd.Revision ?? "none"}{config}.mesh";
                Doc.Files[name] = MeshCache.Write(pd.Mesh, pd.Doc.EffectiveAppearance, pd.Threads);
                Doc.Caches[ck] = name;
            }
            foreach (var c in ComponentsShowing(key)) c.Part.Revision = pd.Revision;
        }
        foreach (var (key, sub) in Subs)
        {
            if (sub.Model?.Doc is not { Revision: { } rev } sd) continue;
            var comps = Doc.Components.Where(c => c.IsAssembly && SubKey(c) == key).ToList();
            foreach (var c in comps) c.Part.Revision = rev;
            // a linked sub-assembly's file kept as it is (it shows from this copy when the file goes missing)
            if (sub.Source != SubSource.File || sd.Id == null || comps.Count == 0) continue;
            var ck = AssemblyDocument.CacheKey(sd.Id, rev);
            if (!KeepMeshes) { drop.Add(ck); continue; }
            if (Doc.Caches.ContainsKey(ck) || Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(comps[0].Part))?.Path is not { } file) continue;
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (DocumentContainer.ReadIdentity(file).revision != rev) continue; // (changed since it was opened)
                var name = $"cache/{sd.Id}-{rev}{AssemblyDocument.FileExtension}";
                Doc.Files[name] = bytes;
                Doc.Caches[ck] = name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var used = Doc.Components.Where(c => c.Part.PartId != null).Select(c => AssemblyDocument.CacheKey(c.Part.PartId!, c.Part.Revision, Configurations.KeyOf(c.Parameters))).ToHashSet();
        foreach (var (ck, name) in Doc.Caches.Where(kv => !used.Contains(kv.Key) || drop.Contains(kv.Key)).ToList())
        {
            Doc.Caches.Remove(ck);
            Doc.Files.Remove(name);
        }
        if (!KeepMeshes) DropEmbeddedMeshes(Doc);
    }

    /// <summary>
    /// The caches of an assembly's embedded sub-assemblies dropped, all the way down, but for their lightweight
    /// components' parts (<see cref="KeepMeshes"/> off). Each copy keeps its revision: only its caches change, not
    /// what it is. Whether any was.
    /// </summary>
    static bool DropEmbeddedMeshes(AssemblyDocument doc)
    {
        bool changed = false;
        foreach (var e in doc.Components.Where(c => c.IsAssembly && c.Part.Embedded != null).Select(c => c.Part.Embedded!).Distinct().ToList())
        {
            if (!doc.Files.TryGetValue(e, out var bytes)) continue;
            AssemblyDocument sd;
            try { sd = AssemblyContainer.Load(bytes, e); }
            catch (InvalidDataException) { continue; } // (a damaged copy: left as it is)
            var light = sd.Components.Where(c => !c.IsAssembly && c.Lightweight && c.Part.PartId != null)
                .Select(c => AssemblyDocument.CacheKey(c.Part.PartId!, c.Part.Revision, Configurations.KeyOf(c.Parameters))).ToHashSet();
            var drop = sd.Caches.Where(kv => !light.Contains(kv.Key)).ToList();
            foreach (var (ck, name) in drop) { sd.Caches.Remove(ck); sd.Files.Remove(name); }
            if (DropEmbeddedMeshes(sd) | drop.Count > 0)
            {
                doc.Files[e] = AssemblyContainer.ToBytes(sd, doc.Files[e]);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// A preview of the assembly (PNG): every component's part as placed, in its color (the image saved in the
    /// file, which Explorer shows, and the list of open documents'). Null when there is nothing to show.
    /// </summary>
    public byte[]? RenderThumbnail(int size = 256)
    {
        var parts = new List<(DisplayMesh, double[])>();
        foreach (var (o, pd) in PlacedParts())
            if (pd.Mesh.Indices.Length > 0) parts.Add((PlacedMesh(pd.Mesh, o.World), o.Color ?? pd.Color));
        return parts.Count == 0 ? null : Thumbnail.Render(parts, size);
    }

    /// <summary>A display mesh's triangles moved by a placement (positions, normals, bounds; the indices shared).</summary>
    public static DisplayMesh PlacedMesh(DisplayMesh m, Transform t)
    {
        var p = new float[m.Positions.Length]; var n = new float[m.Normals.Length];
        var box = Box3.Empty;
        for (int i = 0; i + 2 < m.Positions.Length; i += 3)
        {
            var q = t.Point(new Vec3(m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]));
            p[i] = (float)q.X; p[i + 1] = (float)q.Y; p[i + 2] = (float)q.Z;
            box.Add(q);
        }
        for (int i = 0; i + 2 < m.Normals.Length; i += 3)
        {
            var v = t.Vector(new Vec3(m.Normals[i], m.Normals[i + 1], m.Normals[i + 2]));
            n[i] = (float)v.X; n[i + 1] = (float)v.Y; n[i + 2] = (float)v.Z;
        }
        return new DisplayMesh { Positions = p, Normals = n, Indices = m.Indices, Bounds = box, Volume = m.Volume };
    }

    /// <summary>Saves the assembly (caches refreshed; references made relative to the new place; its preview made).</summary>
    /// <summary>
    /// The assembly as the bytes of its file, to embed in another (an embedded sub-assembly edited in memory): its
    /// display caches and preview made, as a save makes them; its history going on from <paramref name="previous"/>,
    /// the bytes it had there.
    /// </summary>
    public byte[] ToBytes(byte[]? previous)
    {
        Doc.RecordActiveConfiguration();
        RefreshCaches();
        try { Doc.Thumbnail = RenderThumbnail() ?? Doc.Thumbnail; } catch { }
        return AssemblyContainer.ToBytes(Doc, previous);
    }

    public void Save(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        // a copy of the assembly: its own identity
        if (Path != null && !PartIndex.SamePath(Path, path) && Doc.Id != null) { Doc.DerivedFrom = Doc.Id; Doc.Id = null; }
        foreach (var c in Doc.Components)
            if (c.Part.Embedded == null && c.Part.AbsolutePath != null) c.Part.Path = ComponentResolver.Relative(dir, c.Part.AbsolutePath);
        Doc.RecordActiveConfiguration();
        RefreshCaches();
        // a preview is never worth failing a save: the last one is kept if this one cannot be made
        try { Doc.Thumbnail = RenderThumbnail() ?? Doc.Thumbnail; } catch { }
        Doc.Save(path);
        Path = path;
    }
}
