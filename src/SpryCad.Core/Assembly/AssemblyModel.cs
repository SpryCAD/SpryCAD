using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>Where a part's display came from.</summary>
public enum PartSource { File, Embedded, Cache, None }

/// <summary>One part as an assembly shows it: all its bodies as one display mesh, in its color.</summary>
public sealed class PartDisplay
{
    public required DisplayMesh Mesh { get; init; }
    public required double[] Color { get; init; }
    public required PartSource Source { get; init; }
    /// <summary>The part's revision (of its file or embedded copy; of the cache when shown from it).</summary>
    public string? Revision { get; init; }
    /// <summary>The part's document when loaded (not when shown from the cache).</summary>
    public CadDocument? Doc { get; init; }
    /// <summary>Why the part could not be shown as it is (missing, could not be read or rebuilt), if so.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// An open assembly: its document, where its parts were found (<see cref="ComponentResolver"/>) and each part's
/// display — built from the part's file or embedded copy, or taken from the assembly's cache when the part is
/// missing or all its components are lightweight. <see cref="RefreshCaches"/> keeps the caches of the revisions in
/// use (called before saving), so the assembly opens and shares without its parts.
/// </summary>
public sealed class AssemblyModel
{
    public AssemblyDocument Doc { get; }
    public string? Path { get; private set; }
    public PartIndex? Index { get; }
    public ComponentResolver.ResolveResult Resolution { get; private set; } = new();
    /// <summary>By part (<see cref="ComponentResolver.Key"/>).</summary>
    public Dictionary<string, PartDisplay> Parts { get; } = new();
    /// <summary>Lines for the user: parts found elsewhere, missing, given an identity.</summary>
    public List<string> Notes { get; } = new();

    AssemblyModel(AssemblyDocument doc, string? path, PartIndex? index) { Doc = doc; Path = path; Index = index; }

    /// <summary>Opens an assembly: finds its parts and builds (or takes from the cache) each one's display.</summary>
    public static AssemblyModel Open(AssemblyDocument doc, string? path, PartIndex? index = null, IEnumerable<string>? searchRoots = null)
    {
        var m = new AssemblyModel(doc, path, index);
        m.Resolution = ComponentResolver.Resolve(doc, path, index, searchRoots);
        m.Notes.AddRange(m.Resolution.Notes);
        foreach (var key in doc.Components.Select(c => ComponentResolver.Key(c.Part)).Distinct().ToList()) m.LoadPart(key);
        return m;
    }

    public static AssemblyModel New() => new(new AssemblyDocument(), null, null);

    public PartDisplay? DisplayOf(Component c) => Parts.GetValueOrDefault(ComponentResolver.Key(c.Part));

    /// <summary>The components showing a part.</summary>
    public IEnumerable<Component> ComponentsOf(string key) => Doc.Components.Where(c => ComponentResolver.Key(c.Part) == key);

    /// <summary>(Re)builds a part's display: from its file or embedded copy, else from the cache.</summary>
    public void LoadPart(string key)
    {
        var comps = ComponentsOf(key).ToList();
        if (comps.Count == 0) { Parts.Remove(key); return; }
        var r = comps[0].Part;
        var status = Resolution.Parts.GetValueOrDefault(key)?.Status;
        // all its components lightweight, and a cache of the revision they last saw: no need to build it
        bool light = comps.All(c => c.Lightweight) && CachedOf(r) != null;
        string? problem = null;
        if (!light)
        {
            try
            {
                CadDocument? doc = null; PartSource src = PartSource.File;
                if (r.Embedded != null && Doc.Files.TryGetValue(r.Embedded, out var bytes)) { doc = DocumentContainer.Load(bytes, r.Embedded); src = PartSource.Embedded; }
                else if (status is PartStatus.Found or PartStatus.Moved && Resolution.Parts[key].Path is { } file) doc = CadDocument.Load(file);
                if (doc != null)
                {
                    var built = Build(doc);
                    Parts[key] = new PartDisplay { Mesh = built, Color = doc.Color, Source = src, Revision = doc.Revision, Doc = doc };
                    return;
                }
                problem = "not found";
            }
            catch (Exception ex) { problem = ex.Message; }
        }
        // from the cache (lightweight, missing, or it could not be read)
        if (CachedOf(r) is { } cache)
        {
            var e = MeshCache.Read(cache);
            Parts[key] = new PartDisplay { Mesh = e.Mesh, Color = e.Color, Source = PartSource.Cache, Revision = r.Revision, Problem = problem };
            return;
        }
        Parts[key] = new PartDisplay { Mesh = new DisplayMesh(), Color = new[] { 0.74, 0.76, 0.80 }, Source = PartSource.None, Problem = problem ?? "not found" };
    }

    byte[]? CachedOf(PartRef r) =>
        r.PartId != null && Doc.Caches.TryGetValue(AssemblyDocument.CacheKey(r.PartId, r.Revision), out var name) && Doc.Files.TryGetValue(name, out var data) ? data : null;

    /// <summary>A part's display: its rebuilt bodies as one mesh.</summary>
    public static DisplayMesh Build(CadDocument doc)
    {
        var r = new Regenerator().Rebuild(doc);
        return Merge(r.State.Bodies.Select(b => b.Mesh));
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
            var mesh = Build(doc);
            Parts[key] = new PartDisplay { Mesh = mesh, Color = doc.Color, Source = embed ? PartSource.Embedded : PartSource.File, Revision = doc.Revision, Doc = doc };
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
            Parts[key] = new PartDisplay { Mesh = Build(doc), Color = doc.Color, Source = PartSource.Embedded, Revision = doc.Revision, Doc = doc };
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
        var stem = name[..^CadDocument.FileExtension.Length];
        for (int i = 2; ; i++) if (!Doc.Files.ContainsKey($"{stem}-{i}{CadDocument.FileExtension}")) return $"{stem}-{i}{CadDocument.FileExtension}";
    }

    /// <summary>Removes a component (its mates, and an embedded part no other component uses, go with it).</summary>
    public void Remove(Component c)
    {
        Doc.Components.Remove(c);
        Doc.Mates.RemoveAll(m => m.A.Component == c.Id || m.B.Component == c.Id);
        var key = ComponentResolver.Key(c.Part);
        if (!ComponentsOf(key).Any())
        {
            Parts.Remove(key);
            if (c.Part.Embedded != null) Doc.Files.Remove(c.Part.Embedded);
        }
    }

    // ------------------------------------------------------------------------------------------
    // mates

    /// <summary>The assembly's origin planes (mate references with no component).</summary>
    public static readonly string[] OriginPlanes = { "Front", "Top", "Right" };

    /// <summary>A face of a component's part as a mate reference (its part's coordinates); null if it has none.</summary>
    public MateGeom? FaceGeom(string componentId, string face)
    {
        if (Doc.Find(componentId) is not { } c || DisplayOf(c) is not { } pd) return null;
        foreach (var f in pd.Mesh.Faces) if (f.Name == face) return MateGeom.Of(f);
        return null;
    }

    /// <summary>
    /// A reference's geometry in its part's coordinates: the face as the part has it now (the reference's copy
    /// updated), else the copy (lightweight or missing part, or the face gone). Origin planes: the assembly's.
    /// </summary>
    public MateGeom Local(MateRef r)
    {
        if (r.Component == null)
            return r.Face switch
            {
                "Top" => new MateGeom(MateGeomKind.Plane, Vec3.Zero, Vec3.UnitY),
                "Right" => new MateGeom(MateGeomKind.Plane, Vec3.Zero, Vec3.UnitX),
                _ => new MateGeom(MateGeomKind.Plane, Vec3.Zero, Vec3.UnitZ),
            };
        if (FaceGeom(r.Component, r.Face) is { } g)
        {
            r.Kind = g.Kind; r.P = g.P; r.D = g.D; r.R = g.R;
            return g;
        }
        return r.Geom;
    }

    /// <summary>A reference's geometry where its component is placed.</summary>
    public MateGeom World(MateRef r) =>
        r.Component != null && Doc.Find(r.Component) is { } c ? Local(r).Placed(c.Placement.ToTransform()) : Local(r);

    /// <summary>A reference to a face of a component (its geometry copied), or null if that face cannot be mated.</summary>
    public MateRef? RefTo(string? componentId, string face)
    {
        var g = componentId == null ? Local(new MateRef { Face = face }) : FaceGeom(componentId, face);
        return g == null ? null : new MateRef { Component = componentId, Face = face, Kind = g.Kind, P = g.P, D = g.D, R = g.R };
    }

    /// <summary>The last solve's result (mate errors).</summary>
    public MateSolver.Result? LastSolve { get; private set; }

    /// <summary>Moves floating components so the mates hold (<paramref name="onlyMove"/>: just those).</summary>
    public MateSolver.Result SolveMates(ISet<string>? onlyMove = null)
    {
        LastSolve = MateSolver.Solve(Doc, Local, onlyMove);
        return LastSolve;
    }

    /// <summary>The placement of a reference's component (the identity for the assembly's planes).</summary>
    public Transform PlacementOf(MateRef r) => r.Component != null && Doc.Find(r.Component) is { } c ? c.Placement.ToTransform() : Transform.Identity;

    /// <summary>
    /// Adds a mate between two references (the alignment and side nearest how they lie now; Lock, Gear and Screw
    /// recording how the components lie), moving the first reference's component onto it if it floats (else the
    /// second's), then solving all the mates. <paramref name="setup"/>: the mate's options (limits, ratio, …) before.
    /// </summary>
    public Mate AddMate(MateType type, MateRef a, MateRef b, double value = 0, Action<Mate>? setup = null)
    {
        var m = new Mate { Type = type, A = a, B = b, Value = value };
        m.Capture(World(a), World(b), PlacementOf(a), PlacementOf(b));
        setup?.Invoke(m);
        Doc.AddMate(m);
        var first = a.Component != null && Doc.Find(a.Component) is { Fixed: false } ca ? ca.Id
            : b.Component != null && Doc.Find(b.Component) is { Fixed: false } cb ? cb.Id : null;
        if (first != null) SolveMates(new HashSet<string> { first });
        SolveMates();
        return m;
    }

    /// <summary>
    /// The caches for the parts as shown now: each part shown from its file or embedded copy has its revision's
    /// mesh cached (and its components the revision noted); caches no component uses are dropped.
    /// </summary>
    public void RefreshCaches()
    {
        foreach (var (key, pd) in Parts)
        {
            if (pd.Source is not (PartSource.File or PartSource.Embedded) || pd.Doc?.Id is not { } id) continue;
            var ck = AssemblyDocument.CacheKey(id, pd.Revision);
            if (!Doc.Caches.ContainsKey(ck))
            {
                var name = $"cache/{id}-{pd.Revision ?? "none"}.mesh";
                Doc.Files[name] = MeshCache.Write(pd.Mesh, pd.Doc.Color);
                Doc.Caches[ck] = name;
            }
            foreach (var c in ComponentsOf(key)) c.Part.Revision = pd.Revision;
        }
        var used = Doc.Components.Where(c => c.Part.PartId != null).Select(c => AssemblyDocument.CacheKey(c.Part.PartId!, c.Part.Revision)).ToHashSet();
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
        foreach (var c in Doc.Components)
        {
            if (c.Suppressed || DisplayOf(c) is not { } pd || pd.Mesh.Indices.Length == 0) continue;
            parts.Add((PlacedMesh(pd.Mesh, c.Placement.ToTransform()), c.Color ?? pd.Color));
        }
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
        RefreshCaches();
        // a preview is never worth failing a save: the last one is kept if this one cannot be made
        try { Doc.Thumbnail = RenderThumbnail() ?? Doc.Thumbnail; } catch { }
        Doc.Save(path);
        Path = path;
    }
}
