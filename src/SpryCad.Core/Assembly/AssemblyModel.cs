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
    public required double[] Color { get; init; }
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
        var config = Configurations.KeyOf(c.Parameters);
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
        // the part read once (not at all when every configuration is lightweight and cached)
        CadDocument? doc = null; PartSource src = PartSource.File; string? problem = null;
        if (!groups.All(g => g.All(c => c.Lightweight) && CachedOf(g.First()) != null))
        {
            try
            {
                doc = ReadPart(key, out src);
                if (doc == null) problem = "not found";
            }
            catch (Exception ex) { problem = ex.Message; }
        }
        foreach (var g in groups)
        {
            var first = g.First();
            var config = Configurations.KeyOf(first.Parameters);
            bool light = g.All(c => c.Lightweight) && CachedOf(first) != null;
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
                        Parts[g.Key] = new PartDisplay { Mesh = e.Mesh, Threads = e.Threads, Color = doc.Color, Source = src, Revision = doc.Revision, Doc = built, Part = doc, Config = config, Cached = true };
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
                        Mesh = old?.Mesh ?? new DisplayMesh(), Threads = old?.Threads ?? Array.Empty<CosmeticThread>(), Color = doc.Color, Source = src,
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
                Parts[g.Key] = new PartDisplay { Mesh = e.Mesh, Threads = e.Threads, Color = e.Color, Source = PartSource.Cache, Revision = r.Revision, Part = doc, Config = config, Problem = why };
                continue;
            }
            Parts[g.Key] = new PartDisplay { Mesh = new DisplayMesh(), Color = new[] { 0.74, 0.76, 0.80 }, Source = PartSource.None, Part = doc, Config = config, Problem = why ?? "not found" };
        }
    }

    /// <summary>A display built from a part (<paramref name="built"/>: the part as configured), its threads shown as <paramref name="mode"/> says.</summary>
    static PartDisplay BuildDisplay(CadDocument part, CadDocument built, PartSource src, string config, ThreadDisplay mode)
    {
        var (mesh, threads) = Build(built, mode);
        return new PartDisplay { Mesh = mesh, Threads = threads, Color = part.Color, Source = src, Revision = part.Revision, Doc = built, Part = part, Config = config };
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
            Mesh = p.Mesh, Threads = p.Threads, Color = p.Color, Source = p.Mesh.Indices.Length > 0 ? PartSource.Cache : PartSource.None,
            Revision = p.Revision, Part = p.Part, Config = p.Config, Problem = problem ?? "could not be built",
        };
        return true;
    }

    /// <summary>A part's document from its embedded copy or its file (null: not found).</summary>
    CadDocument? ReadPart(string key, out PartSource source)
    {
        source = PartSource.File;
        if (ComponentsOf(key).FirstOrDefault()?.Part is not { } r) return null;
        if (r.Embedded != null && Doc.Files.TryGetValue(r.Embedded, out var bytes)) { source = PartSource.Embedded; return DocumentContainer.Load(bytes, r.Embedded); }
        if (Resolution.Parts.GetValueOrDefault(key) is { Status: PartStatus.Found or PartStatus.Moved, Path: { } file }) return CadDocument.Load(file);
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
            doc.Save(partPath, DocumentContainer.IsContainer(partPath));
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
            Parts[key] = new PartDisplay { Mesh = mesh, Threads = threads, Color = doc.Color, Source = embed ? PartSource.Embedded : PartSource.File, Revision = doc.Revision, Doc = doc, Part = doc };
        }
        return c;
    }

    /// <summary>
    /// Adds a component of a part document embedded in the assembly (the document saved into it once per part:
    /// further components of it share the copy). The document gets an identity if it has none.
    /// </summary>
    public Component InsertEmbedded(CadDocument doc, string name, Transform placement)
    {
        var existing = doc.Id != null ? Doc.Components.Select(c => c.Part).FirstOrDefault(p => p.Embedded != null && p.PartId == doc.Id) : null;
        PartRef r;
        if (existing != null) r = existing.Clone();
        else
        {
            // saved as a part file would be (its identity and revision given), kept inside the assembly
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fc-embed-" + Guid.NewGuid().ToString("N") + CadDocument.FileExtension);
            try { doc.Save(tmp, container: true); Doc.Files[UniqueName("parts/" + SafeFileName(name) + CadDocument.FileExtension)] = File.ReadAllBytes(tmp); }
            finally { try { File.Delete(tmp); } catch { } }
            var file = Doc.Files.Keys.Last(k => k.StartsWith("parts/" + SafeFileName(name), StringComparison.Ordinal));
            r = new PartRef { PartId = doc.Id, Revision = doc.Revision, Embedded = file };
        }
        var c = Doc.AddComponent(r, placement, name);
        var key = ComponentResolver.Key(r);
        if (!Resolution.Parts.ContainsKey(key)) Resolution.Parts[key] = new PartResolution(PartStatus.Embedded, null, false, null);
        if (!Parts.ContainsKey(key))
        {
            var (mesh, threads) = Build(doc, Threads);
            Parts[key] = new PartDisplay { Mesh = mesh, Threads = threads, Color = doc.Color, Source = PartSource.Embedded, Revision = doc.Revision, Doc = doc, Part = doc };
        }
        return c;
    }

    static string SafeFileName(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        var s = new string(name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return s.Length == 0 ? "Part" : s;
    }

    /// <summary>
    /// An assembly from a STEP assembly file: each of its parts a part document embedded in the assembly (an Import
    /// of that part's solids, in its own coordinates), each use of a part a component where the file places it. The
    /// first component is fixed and every other one locked to it, so the assembly stays as it was designed until a
    /// lock is deleted (then mate it as wanted). Null when the file is not an assembly (or holds one part, once).
    /// </summary>
    public static AssemblyModel? FromStep(byte[] data, string name)
    {
        var res = StepImport.ReadAssembly(data, "S.");
        // no assembly structure, or a single use of a single part (a part, as STEP parts are opened)
        if (res == null || res.Occurrences.Count < 2) return null;
        var m = New();
        m.Notes.AddRange(res.Warnings);
        // a part document per STEP part: its solids written to a STEP of their own and imported
        var docs = new CadDocument?[res.Parts.Count];
        var names = new string[res.Parts.Count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < res.Parts.Count; i++)
        {
            var part = res.Parts[i];
            var n = part.Name;
            for (int k = 2; !used.Add(n); k++) n = $"{part.Name} ({k})";
            names[i] = n;
            using var ms = new MemoryStream();
            StepWriter.Write(ms, part.Solids.Select(sd => (sd.Name, Kernel.Native.NTess.ToSolid(sd.Body))), n);
            var bytes = ms.ToArray();
            var doc = new CadDocument();
            var file = "imports/" + SafeFileName(n) + ".step";
            doc.AddFile(file, bytes);
            doc.AddFeature(new ImportFeature { File = file, Sha256 = DocumentContainer.Hash(bytes) });
            docs[i] = doc;
        }
        Component? first = null;
        foreach (var o in res.Occurrences)
        {
            var c = m.InsertEmbedded(docs[o.Part]!, names[o.Part], o.Placement);
            if (first == null) { first = c; continue; }
            // held where the file put it: locked to the first component
            // (a lock holds the components themselves: no face is needed)
            m.AddMate(MateType.Lock, new MateRef { Component = c.Id, D = Vec3.UnitZ }, new MateRef { Component = first.Id, D = Vec3.UnitZ });
        }
        return m;
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

    /// <summary>A component dragged: the mates solved with its grabbed point pulled toward the cursor's ray.</summary>
    public MateSolver.Result SolveDrag(MateSolver.Pull pull)
    {
        LastSolve = Solve(null, pull);
        ApplyPatterns();
        return LastSolve;
    }

    /// <summary>The placement of a reference's component in the assembly (the identity for the assembly's planes).</summary>
    public Transform PlacementOf(MateRef r) => r.Component != null && Find(r.Component) is { } o ? o.World : Transform.Identity;

    /// <summary>
    /// Adds a mate between two references (the alignment and side nearest how they lie now; Lock, Gear and Screw
    /// recording how the components lie), moving the first reference's component onto it if it floats (else the
    /// second's), then solving all the mates. <paramref name="setup"/>: the mate's options (limits, ratio, …) before.
    /// </summary>
    public Mate AddMate(MateType type, MateRef a, MateRef b, double value = 0, Action<Mate>? setup = null)
    {
        var m = new Mate { Type = type, A = a, B = b, Value = value };
        Mate.Normalize(m);
        m.Capture(World(a), World(b), PlacementOf(a), PlacementOf(b));
        setup?.Invoke(m);
        Doc.AddMate(m);
        Settle(a, b);
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
    /// The caches for the parts as shown now: each part shown from its file or embedded copy has its revision's
    /// mesh cached (and its components the revision noted), made again when an older version of the code made it; caches
    /// no component uses are dropped. Displays still waiting to be built keep the cache they show. Displays with their
    /// threads shown otherwise than as set (<see cref="Threads"/>) are not cached: the caches they had are kept.
    /// </summary>
    public void RefreshCaches()
    {
        foreach (var (key, pd) in Parts)
        {
            if (pd.Building || pd.Source is not (PartSource.File or PartSource.Embedded) || pd.Doc?.Id is not { } id) continue;
            if (Threads != ThreadDisplay.AsSet)
            {
                foreach (var c in ComponentsShowing(key)) c.Part.Revision = pd.Revision;
                continue;
            }
            var ck = AssemblyDocument.CacheKey(id, pd.Revision, pd.Config);
            var stale = Doc.Caches.TryGetValue(ck, out var had) && (!Doc.Files.TryGetValue(had, out var bytes) || MeshCache.VersionOf(bytes) != GeometryVersion.Current);
            if (!Doc.Caches.ContainsKey(ck) || stale)
            {
                // (a configuration's: named by a hash of it)
                var config = pd.Config.Length > 0 ? "-" + DocumentContainer.Hash(System.Text.Encoding.UTF8.GetBytes(pd.Config))[..12] : "";
                var name = $"cache/{id}-{pd.Revision ?? "none"}{config}.mesh";
                Doc.Files[name] = MeshCache.Write(pd.Mesh, pd.Doc.Color, pd.Threads);
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
        foreach (var (ck, name) in Doc.Caches.Where(kv => !used.Contains(kv.Key)).ToList())
        {
            Doc.Caches.Remove(ck);
            Doc.Files.Remove(name);
        }
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
