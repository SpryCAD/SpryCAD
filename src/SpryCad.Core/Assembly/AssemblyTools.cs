using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Core.Assembly;

public enum BomKind
{
    /// <summary>The assembly's own components (sub-assemblies as one line each).</summary>
    TopLevel,
    /// <summary>Every level: each sub-assembly's components under it, counted per sub-assembly.</summary>
    Indented,
    /// <summary>Every part at every level, counted together (no sub-assemblies).</summary>
    PartsOnly,
}

/// <summary>One line of a bill of materials.</summary>
public sealed record BomRow(string Item, int Level, string Name, string Configuration, int Quantity, bool IsAssembly, string? File, string? Material);

/// <summary>
/// Bills of materials: the components counted (suppressed ones left out; hidden ones counted), a line for each part or
/// sub-assembly in each configuration.
/// </summary>
public static class Bom
{
    public static List<BomRow> Build(AssemblyModel m, BomKind kind)
    {
        var rows = new List<BomRow>();
        if (kind == BomKind.PartsOnly)
        {
            int item = 0;
            foreach (var g in m.Occurrences().Where(o => !o.Component.IsAssembly).GroupBy(o => AssemblyModel.DisplayKey(o.Component)))
            {
                var o = g.First();
                rows.Add(Row((++item).ToString(CultureInfo.InvariantCulture), 0, o, g.Count()));
            }
            return rows;
        }
        Level(m, m.Occurrences().Where(o => o.Rel == null).ToList(), "", 0, kind == BomKind.Indented, rows);
        return rows;
    }

    static void Level(AssemblyModel m, List<Occurrence> here, string prefix, int level, bool deep, List<BomRow> rows)
    {
        int item = 0;
        foreach (var g in here.GroupBy(o => o.Component.IsAssembly ? AssemblyModel.SubKey(o.Component) : AssemblyModel.DisplayKey(o.Component)))
        {
            var o = g.First();
            var no = prefix + (++item).ToString(CultureInfo.InvariantCulture);
            rows.Add(Row(no, level, o, g.Count()));
            if (!deep || !o.Component.IsAssembly) continue;
            // its components as the first of them shows them, counted per sub-assembly
            var children = m.Occurrences().Where(x => x.Path.StartsWith(o.Path + "/", StringComparison.Ordinal) && x.Path.IndexOf('/', o.Path.Length + 1) < 0).ToList();
            Level(m, children, no + ".", level + 1, true, rows);
        }
    }

    static BomRow Row(string item, int level, Occurrence o, int qty)
    {
        var c = o.Component;
        var r = c.Part;
        var file = r.Embedded == null ? o.Owner.Resolution.Parts.GetValueOrDefault(ComponentResolver.Key(r))?.Path ?? r.AbsolutePath ?? r.Path : null;
        var name = System.IO.Path.GetFileNameWithoutExtension(r.Path ?? r.AbsolutePath ?? r.Embedded ?? c.Name);
        if (string.IsNullOrEmpty(name)) name = c.Name.Split('<')[0];
        // a fastener by its standard and designation, with its length ("ISO 4762 M8x30"), not by its file
        if (Library.Fasteners.Of(o.Display?.Part ?? o.Display?.Doc) is { } fastener) name = fastener.Designation;
        string config, material = "";
        if (c.IsAssembly) config = c.Configuration ?? "";
        else
        {
            config = c.Parameters != null ? o.Owner.ConfigLabel(c) : "";
            material = o.Display?.Part?.Material ?? o.Display?.Doc?.Material ?? "";
        }
        return new BomRow(item, level, name, config, qty, c.IsAssembly, file, material.Length > 0 ? material : null);
    }

    /// <summary>The rows as CSV (Item, Part, Configuration, Quantity, Material, File), quoted where needed.</summary>
    public static string ToCsv(IEnumerable<BomRow> rows)
    {
        static string Q(string? s) => s == null ? "" : s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder("Item,Part,Configuration,Quantity,Material,File\n");
        foreach (var r in rows)
            sb.Append(Q(r.Item)).Append(',').Append(Q(new string(' ', 2 * r.Level) + r.Name)).Append(',').Append(Q(r.Configuration)).Append(',')
              .Append(r.Quantity.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Q(r.Material)).Append(',').Append(Q(r.File)).Append('\n');
        return sb.ToString();
    }
}

/// <summary>The solids of a part display, built once (from its document) and kept as long as the display is.</summary>
/// <summary>
/// Work on many parts at once (rebuilds, mostly): in parallel, leaving a core for the UI; items sharing a key (the same
/// part document, which configured displays of one part share) one after another, so no document is read by two
/// rebuilds at once.
/// </summary>
public static class PartWork
{
    public static void Each<T>(IReadOnlyList<T> items, Func<T, object> key, Action<T> work, CancellationToken ct = default)
    {
        var groups = items.GroupBy(key, ReferenceEqualityComparer.Instance).Select(g => g.ToList()).ToList();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = ct };
        Parallel.ForEach(groups, options, g => { foreach (var x in g) { ct.ThrowIfCancellationRequested(); work(x); } });
    }
}

public static class AssemblySolids
{
    static readonly ConditionalWeakTable<PartDisplay, List<Solid>> Cache = new();

    /// <summary>The solids of these parts built (those not built yet), in parallel (<see cref="PartWork"/>).</summary>
    public static void Prepare(IEnumerable<PartDisplay> displays, CancellationToken ct = default)
    {
        var todo = displays.Where(pd => pd.Doc != null).Distinct().Where(pd => { lock (Cache) return !Cache.TryGetValue(pd, out _); }).ToList();
        PartWork.Each(todo, pd => pd.Doc!, pd => Of(pd), ct);
    }

    /// <summary>The part's solids in its own coordinates (none when it is shown only from a copy).</summary>
    public static List<Solid> Of(PartDisplay pd)
    {
        if (pd.Doc == null) return new List<Solid>();
        lock (Cache)
            if (Cache.TryGetValue(pd, out var l)) return l;
        var solids = new Regenerator().Rebuild(pd.Doc).State.Bodies.Select(b => b.Solid).Where(s => !s.IsEmpty).ToList();
        lock (Cache) Cache.AddOrUpdate(pd, solids);
        return solids;
    }
}

/// <summary>An assembly's mass properties, every part with its own material's density.</summary>
public sealed class AssemblyMassResult
{
    /// <summary>Grams (model units mm), mm³, mm².</summary>
    public double Mass, Volume, Area;
    public Vec3 Center;
    /// <summary>Inertia tensor about the center of mass, assembly axes (g·mm²; products negated, as <see cref="MassProps"/>).</summary>
    public double[,] Inertia = new double[3, 3];
    public double[] Principal = new double[3];
    public Vec3[] Axes = { Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ };
    public int Parts;
    /// <summary>Parts measured from their display mesh only (shown from a copy: not exact), by name.</summary>
    public List<string> Approximate = new();
}

public static class AssemblyMass
{
    static readonly ConditionalWeakTable<PartDisplay, MassProps> Cache = new();

    /// <summary>A part's mass properties at unit density in its own coordinates (exact from its solids, else its mesh).</summary>
    static MassProps Of(PartDisplay pd, out bool exact)
    {
        exact = pd.Doc != null;
        lock (Cache) if (Cache.TryGetValue(pd, out var c)) return c;
        var mp = new MassProps();
        if (pd.Doc != null)
            foreach (var s in AssemblySolids.Of(pd))
            {
                if (s.Native is { } nb && NMass.Integrate(nb) is { } ex) mp.AddIntegrals(ex.S, ex.Area);
                else exact = false;
            }
        if (!exact || mp.Volume == 0 && pd.Doc == null) { mp = new MassProps(); mp.Add(pd.Mesh.Positions, pd.Mesh.Indices); }
        mp.Finish();
        lock (Cache) Cache.AddOrUpdate(pd, mp);
        return mp;
    }

    /// <summary>The mass properties of every part shown (suppressed ones left out, hidden ones counted), where it is.</summary>
    public static AssemblyMassResult Of(AssemblyModel m)
    {
        var r = new AssemblyMassResult();
        var items = new List<(double mass, Vec3 c, double[,] i)>();
        // each part's measured once, in parallel
        var placed = m.PlacedParts(hidden: true).ToList();
        PartWork.Each(placed.Select(x => x.pd).Distinct().ToList(), pd => pd.Doc ?? (object)pd, pd => Of(pd, out _));
        foreach (var (o, pd) in placed)
        {
            var mp = Of(pd, out var exact);
            if (mp.Volume <= 0) continue;
            if (!exact) r.Approximate.Add(m.NameAt(o.Path));
            var rho = (pd.Part?.Density ?? pd.Doc?.Density ?? CadDocument.DefaultDensity) * 1e-6; // kg/m³ to g/mm³
            var t = o.World;
            var mass = mp.Volume * rho;
            // the tensor turned into the assembly's axes: R I Rᵀ
            var R = new double[3, 3] { { t.M00, t.M01, t.M02 }, { t.M10, t.M11, t.M12 }, { t.M20, t.M21, t.M22 } };
            var I = new double[3, 3];
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) for (int l = 0; l < 3; l++) s += R[a, k] * mp.AtCenter[k, l] * R[b, l];
                    I[a, b] = s * rho;
                }
            items.Add((mass, t.Point(mp.Center), I));
            r.Mass += mass; r.Volume += mp.Volume; r.Area += mp.SurfaceArea;
            r.Parts++;
        }
        if (r.Mass <= 0) return r;
        var com = Vec3.Zero;
        foreach (var x in items) com += x.c * x.mass;
        r.Center = com / r.Mass;
        // each part's tensor moved to the assembly's center of mass (parallel axis)
        foreach (var (mass, c, I) in items)
        {
            var d = c - r.Center;
            double[] dv = { d.X, d.Y, d.Z };
            var dd = Vec3.Dot(d, d);
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                    r.Inertia[a, b] += I[a, b] + mass * ((a == b ? dd : 0) - dv[a] * dv[b]);
        }
        (r.Principal, r.Axes) = MassProps.Eigen(r.Inertia);
        return r;
    }
}

/// <summary>Two parts of an assembly overlapping: how much (mm³) and where.</summary>
public sealed record Clash(string A, string B, double Volume, Box3 Bounds);

/// <summary>
/// Interference detection: every two parts whose boxes overlap intersected exactly; those sharing volume (not just
/// touching) reported, largest first.
/// </summary>
public static class Interference
{
    /// <summary>A part to check: its path, display and placement (taken on the UI thread, checked off it).</summary>
    public sealed record Item(string Path, PartDisplay Display, Transform World);

    /// <summary>The parts of an assembly to check (suppressed ones left out).</summary>
    public static List<Item> Items(AssemblyModel m) => m.PlacedParts(hidden: true).Select(x => new Item(x.o.Path, x.pd, x.o.World)).ToList();

    /// <summary>
    /// The clashes among <paramref name="items"/>. <paramref name="progress"/>: (pairs checked, pairs to check);
    /// <paramref name="notes"/>: parts that could not be checked (shown only from a copy) and pairs that failed.
    /// With <paramref name="withinSubAssemblies"/> false, parts of one sub-assembly are not checked against each other.
    /// </summary>
    public static List<Clash> Find(IReadOnlyList<Item> items, List<string> notes, bool withinSubAssemblies = true, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var bodies = new List<(Item item, NBody body, Box3 box)>();
        AssemblySolids.Prepare(items.Select(it => it.Display), ct);
        foreach (var it in items)
        {
            var solids = AssemblySolids.Of(it.Display);
            if (solids.Count == 0) { notes.Add($"{it.Path}: shown only from a copy (not checked)"); continue; }
            foreach (var s in solids)
                if (s.Native is { } nb)
                {
                    var tb = nb.Transformed(it.World);
                    bodies.Add((it, tb, tb.Bounds));
                }
        }
        var pairs = new List<(int, int)>();
        for (int i = 0; i < bodies.Count; i++)
            for (int j = i + 1; j < bodies.Count; j++)
            {
                if (bodies[i].item == bodies[j].item || !bodies[i].box.Overlaps(bodies[j].box)) continue;
                if (!withinSubAssemblies && TopOf(bodies[i].item.Path) == TopOf(bodies[j].item.Path) && bodies[i].item.Path.Contains('/')) continue;
                pairs.Add((i, j));
            }
        // each pair's common volume, in parallel (the pairs' results then taken in order: the same list either way)
        var common = new (double v, Box3 b, string? failed)[pairs.Count];
        int done = 0;
        progress?.Invoke(0, pairs.Count);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = ct };
        Parallel.For(0, pairs.Count, options, k =>
        {
            var (i, j) = pairs[k];
            try
            {
                var c = NBoolean.Run(bodies[i].body, bodies[j].body, BoolOp.Intersect);
                if (c.Faces.Count > 0 && NMass.Of(c) is { } mp && mp.Volume >= 1e-6) common[k] = (mp.Volume, c.Bounds, null);
            }
            catch (Exception ex) when (ex is NBooleanException or InvalidOperationException or ArgumentException)
            {
                common[k] = (0, Box3.Empty, ex.Message);
            }
            progress?.Invoke(Interlocked.Increment(ref done), pairs.Count);
        });
        var found = new Dictionary<(string, string), (double v, Box3 b)>();
        for (int k = 0; k < pairs.Count; k++)
        {
            var (i, j) = pairs[k];
            if (common[k].failed is { } why) { notes.Add($"{bodies[i].item.Path} and {bodies[j].item.Path}: could not be checked ({why})"); continue; }
            if (common[k].v == 0) continue;
            var key = (bodies[i].item.Path, bodies[j].item.Path);
            var had = found.GetValueOrDefault(key);
            var b = had.b; b.Add(common[k].b);
            found[key] = (had.v + common[k].v, had.v == 0 ? common[k].b : b);
        }
        return found.Select(kv => new Clash(kv.Key.Item1, kv.Key.Item2, kv.Value.v, kv.Value.b)).OrderByDescending(c => c.Volume).ToList();
    }

    static string TopOf(string path) => path.IndexOf('/') is var i and > 0 ? path[..i] : path;
}

/// <summary>
/// Pack and Go: an assembly copied with every file it uses (parts and sub-assemblies at every depth) to a new folder,
/// keeping their folders' layout (or all in one folder), optionally renamed; the copied assemblies refer to the copies.
/// </summary>
public static class PackAndGo
{
    public sealed record Item(string From, string To);

    /// <summary>What would be copied where. The assembly must be saved (its file is copied as saved).</summary>
    public static List<Item> Plan(AssemblyModel m, string destDir, string prefix = "", string suffix = "", bool flatten = false)
    {
        if (m.Path == null) throw new InvalidOperationException("Save the assembly first.");
        destDir = System.IO.Path.GetFullPath(destDir);
        var files = new List<string> { System.IO.Path.GetFullPath(m.Path) };
        foreach (var f in m.SourceFiles()) if (!files.Any(x => PartIndex.SamePath(x, f))) files.Add(System.IO.Path.GetFullPath(f));
        var root = flatten ? null : CommonFolder(files);
        var items = new List<Item>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var rel = root != null ? System.IO.Path.GetRelativePath(root, System.IO.Path.GetDirectoryName(f)!) : ".";
            var name = prefix + System.IO.Path.GetFileNameWithoutExtension(f) + suffix;
            var ext = System.IO.Path.GetExtension(f);
            var to = System.IO.Path.GetFullPath(System.IO.Path.Combine(destDir, rel, name + ext));
            for (int i = 2; !taken.Add(to); i++) to = System.IO.Path.GetFullPath(System.IO.Path.Combine(destDir, rel, $"{name}-{i}{ext}"));
            items.Add(new Item(f, to));
        }
        return items;
    }

    /// <summary>The folder holding all the files (null when they are on different drives).</summary>
    static string? CommonFolder(List<string> files)
    {
        var dirs = files.Select(f => System.IO.Path.GetDirectoryName(f)!).ToList();
        var common = dirs[0];
        foreach (var d in dirs.Skip(1))
            while (common != null && !(d + System.IO.Path.DirectorySeparatorChar).StartsWith(common.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                common = System.IO.Path.GetDirectoryName(common);
        return common;
    }

    /// <summary>Copies the files planned; the assemblies among them rewritten to refer to the copies. Existing files are overwritten.</summary>
    public static void Run(IReadOnlyList<Item> plan)
    {
        var byFrom = plan.ToDictionary(i => i.From, i => i.To, StringComparer.OrdinalIgnoreCase);
        // by identity too (a reference whose paths are stale still finds its copy)
        var byId = new Dictionary<string, string>();
        foreach (var i in plan) if (DocumentContainer.ReadIdentity(i.From).id is { } id) byId.TryAdd(id, i.To);
        foreach (var i in plan)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(i.To)!);
            if (!AssemblyModel.IsAssemblyFile(i.From)) { File.Copy(i.From, i.To, overwrite: true); continue; }
            var doc = AssemblyDocument.Load(i.From);
            Rewrite(doc, System.IO.Path.GetDirectoryName(i.From)!, System.IO.Path.GetDirectoryName(i.To)!, byFrom, byId);
            AssemblyContainer.Save(doc, i.To);
        }
    }

    static void Rewrite(AssemblyDocument doc, string fromDir, string toDir, Dictionary<string, string> byFrom, Dictionary<string, string> byId)
    {
        foreach (var c in doc.Components)
        {
            var r = c.Part;
            if (r.Embedded != null)
            {
                // an embedded sub-assembly's own links (relative to the assembly holding it)
                if (r.Assembly && doc.Files.TryGetValue(r.Embedded, out var bytes))
                {
                    var sd = AssemblyContainer.Load(bytes, r.Embedded);
                    Rewrite(sd, fromDir, toDir, byFrom, byId);
                    doc.Files[r.Embedded] = AssemblyContainer.ToBytes(sd, doc.Files[r.Embedded]);
                    foreach (var o in doc.Components.Where(x => x.Part.Embedded == r.Embedded)) o.Part.Revision = sd.Revision;
                }
                continue;
            }
            string? to = null;
            foreach (var cand in new[] { r.Path != null ? System.IO.Path.GetFullPath(System.IO.Path.Combine(fromDir, r.Path)) : null, r.AbsolutePath })
                if (cand != null && byFrom.TryGetValue(cand, out var t)) { to = t; break; }
            if (to == null && r.PartId != null) byId.TryGetValue(r.PartId, out to);
            if (to == null) continue;
            r.AbsolutePath = to;
            r.Path = ComponentResolver.Relative(toDir, to);
        }
    }

    /// <summary>The copied folder zipped (the zip replaces any file there).</summary>
    public static void Zip(string folder, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        System.IO.Compression.ZipFile.CreateFromDirectory(folder, zipPath);
    }
}
