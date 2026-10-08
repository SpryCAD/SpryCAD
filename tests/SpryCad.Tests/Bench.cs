using System.Diagnostics;
using SpryCad.Kernel.Native;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Rebuild benchmark: <c>dotnet run -c Release -- bench file.sprt [runs]</c>.</summary>
static class Bench
{
    public static void Run(string file, int runs)
    {
        var json = CadDocument.Load(file).ToJson(); // plain or container
        Console.WriteLine($"bench {Path.GetFileName(file)}");
        var times = new List<double>();
        var tessTimes = new List<double>();
        var meshTimes = new List<double>();
        Dictionary<string, List<double>> perFeature = new();
        for (int i = 0; i < runs + 1; i++)
        {
            var doc = CadDocument.FromJson(json);
            Csg.ClearCache(); // measure real computation, not cache hits across runs
            GC.Collect(); GC.WaitForPendingFinalizers();
            Prof.Reset(); Prof.Enabled = i > 0;
            using var allocs = i == runs && Environment.GetEnvironmentVariable("FC_ALLOC") != null ? new AllocSampler() : null;
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long alloc = GC.GetTotalAllocatedBytes();
            var sw = Stopwatch.StartNew();
            var r = new Regenerator().Rebuild(doc);
            var ms = sw.Elapsed.TotalMilliseconds;
            var gcText = $"{(GC.GetTotalAllocatedBytes() - alloc) / 1e6:0} MB allocated, collections gen0 {GC.CollectionCount(0) - gc0} gen1 {GC.CollectionCount(1) - gc1} gen2 {GC.CollectionCount(2) - gc2}";
            var tsw = Stopwatch.StartNew();
            int facets = r.State.Bodies.Sum(b => b.Solid.Faces.Count); // what the viewport needs
            tessTimes.Add(tsw.Elapsed.TotalMilliseconds);
            var msw = Stopwatch.StartNew();
            using (Prof.Time("display.mesh")) foreach (var b in r.State.Bodies) _ = b.Mesh; // display mesh (GPU buffers source)
            meshTimes.Add(msw.Elapsed.TotalMilliseconds);
            if (i == 0) { Console.WriteLine($"  cold: {ms:0} ms"); continue; }
            times.Add(ms);
            if (Environment.GetEnvironmentVariable("FC_RUNS") != null) Console.WriteLine($"    run {i}: {ms:0.0} ms");
            foreach (var f in doc.Features)
            {
                if (!perFeature.TryGetValue(f.Name, out var l)) perFeature[f.Name] = l = new();
                l.Add(r.Status[f.Id].Milliseconds);
            }
            if (i == runs)
            {
                Console.WriteLine($"  warm full rebuild: median {Median(times):0.0} ms (min {times.Min():0.0}), + display tessellation median {Median(tessTimes):0.0} ms + display mesh {Median(meshTimes):0.0} ms");
                Console.WriteLine($"  rebuild (last run): {gcText}");
                allocs?.Print(25);
                foreach (var (name, l) in perFeature) Console.WriteLine($"    {name,-20} {Median(l),8:0.0} ms");
                Console.WriteLine("  profile (last run):");
                foreach (var (name, pms, calls) in Prof.Results()) Console.WriteLine($"    {name,-24} {pms,8:0.0} ms  x{calls}");
                // incremental: change the last feature only
                var reg = new Regenerator(); reg.Rebuild(doc);
                var last = doc.Features[^1];
                last.Name += " ";
                Prof.Reset(); Csg.ClearCache();
                var sw2 = Stopwatch.StartNew();
                var r2 = reg.Rebuild(doc);
                Console.WriteLine($"  incremental (last feature): {sw2.Elapsed.TotalMilliseconds:0.0} ms, rebuilt {r2.Rebuilt}");
                if (Environment.GetEnvironmentVariable("FC_PROF_LAST") != null)
                    foreach (var (name, pms, calls) in Prof.Results()) Console.WriteLine($"      {name,-24} {pms,8:0.0} ms  x{calls}");
                // edit cost per feature: change a numeric parameter of feature k to a new value (best of 3)
                Console.WriteLine("  edit cost (change a parameter of the feature):");
                int bump = 0;
                for (int k = 0; k < doc.Features.Count; k++)
                {
                    var prop = NumericParam(doc.Features[k]);
                    if (prop == null) continue;
                    var orig = (string)prop.GetValue(doc.Features[k])!;
                    double best = double.MaxValue; int rebuilt = 0;
                    for (int rep = 0; rep < 3; rep++)
                    {
                        reg.Rebuild(doc);
                        var v = double.Parse(orig, System.Globalization.CultureInfo.InvariantCulture) * (1 + 0.002 * ++bump);
                        prop.SetValue(doc.Features[k], v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        bool prof = rep == 0 && Environment.GetEnvironmentVariable("FC_PROF_EDIT") == doc.Features[k].Name;
                        if (prof) { Prof.Reset(); Prof.Enabled = true; }
                        var sw3 = Stopwatch.StartNew();
                        var r3 = reg.Rebuild(doc);
                        if (prof)
                        {
                            Prof.Enabled = false;
                            foreach (var (name, pms, calls) in Prof.Results()) Console.WriteLine($"      {name,-24} {pms,8:0.0} ms  x{calls}");
                        }
                        best = Math.Min(best, sw3.Elapsed.TotalMilliseconds);
                        rebuilt = r3.Rebuilt;
                        if (rep == 0 && sw3.Elapsed.TotalMilliseconds > 500)
                            foreach (var ff in doc.Features)
                                if (r3.Status[ff.Id].Milliseconds > 100)
                                    Console.WriteLine($"      slow: {ff.Name} {r3.Status[ff.Id].Milliseconds:0} ms, fallbacks={Csg.NativeFallback} ({Csg.LastFallbackReason}) warn={string.Join(";", r3.Status[ff.Id].Warnings)}");
                        if (!r3.Status.Values.All(x => x.Ok)) Console.WriteLine($"      ({doc.Features[k].Name} edit failed: {string.Join("; ", r3.Status.Values.Where(x => !x.Ok).Select(x => x.Error))})");
                    }
                    prop.SetValue(doc.Features[k], orig);
                    Console.WriteLine($"    {doc.Features[k].Name,-16} {prop.Name,-12} {best,8:0.0} ms  ({rebuilt} features)");
                }
            }
        }
        Prof.Enabled = false;
    }

    /// <summary>A plain-number string parameter of a feature (depth, radius, diameter, ...), if any.</summary>
    static System.Reflection.PropertyInfo? NumericParam(SpryCad.Core.Model.Feature f)
        => f.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.Name is not ("Name" or "Id"))
            .FirstOrDefault(p => p.GetValue(f) is string v && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0);

    /// <summary>
    /// <c>-- repro file.sprt FeatureName Prop value</c>: rebuilds with one parameter changed and the boolean trace
    /// of the failing (fallback) operations written to stdout.
    /// </summary>
    public static void Repro(string file, params string[] edits)
    {
        var doc = CadDocument.Load(file);
        // edits: (feature, property, value) triples; feature "=" sets the equation named by property
        for (int i = 0; i + 2 < edits.Length; i += 3)
        {
            var (feature, prop, value) = (edits[i], edits[i + 1], edits[i + 2]);
            if (feature == "=") { doc.Equations.First(e => e.Name == prop).Expr = value; continue; }
            var f = doc.Features.First(x => x.Name == feature);
            var pi = f.GetType().GetProperty(prop)!;
            pi.SetValue(f, Convert.ChangeType(value, pi.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
        }
        var log = new List<string>();
        SpryCad.Kernel.Native.NBoolean.Trace = m => log.Add(m);
        int before = Csg.NativeFallback;
        bool prof = Environment.GetEnvironmentVariable("FC_PROF") != null;
        if (prof) { Prof.Reset(); Prof.Enabled = true; }
        var r = new Regenerator().Rebuild(doc);
        if (prof)
        {
            Prof.Enabled = false;
            foreach (var (name, pms, calls) in Prof.Results().Take(200)) Console.WriteLine($"      {name,-24} {pms,8:0.0} ms  x{calls}");
        }
        SpryCad.Kernel.Native.NBoolean.Trace = null;
        foreach (var ff in doc.Features) Console.WriteLine($"  {ff.Name,-16} {r.Status[ff.Id].Milliseconds,8:0} ms {r.Status[ff.Id].Error} {string.Join("; ", r.Status[ff.Id].Warnings)}");
        Console.WriteLine($"fallbacks: {Csg.NativeFallback - before}, last: {Csg.LastFallbackReason}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "fc-repro-trace.txt"), log);
        Console.WriteLine($"trace: {log.Count} lines -> %TEMP%/fc-repro-trace.txt");
    }

    /// <summary>
    /// <c>-- featprof file.sprt Feature [runs]</c>: the time of one feature (everything before it cached), with the
    /// profile of its last run.
    /// </summary>
    public static void FeatureProfile(string file, string name, int runs)
    {
        var doc = CadDocument.Load(file);
        int idx = doc.Features.FindLastIndex(f => f.Name == name);
        if (idx < 0) { Console.WriteLine($"no feature {name}"); return; }
        var times = new List<double>();
        for (int run = 0; run <= runs; run++)
        {
            Csg.ClearCache();
            var reg = new Regenerator();
            reg.Rebuild(doc, idx);
            Prof.Reset(); Prof.Enabled = run == runs;
            var sw = Stopwatch.StartNew();
            var r = reg.Rebuild(doc, idx + 1);
            Prof.Enabled = false;
            if (run > 0) times.Add(sw.Elapsed.TotalMilliseconds);
            if (run == runs)
            {
                var st = r.Status[doc.Features[idx].Id];
                Console.WriteLine($"{name}: median {Median(times):0.0} ms {st.Error}");
                foreach (var (n, pms, calls) in Prof.Results().Take(40)) Console.WriteLine($"  {n,-28} {pms,8:0.0} ms  x{calls}");
            }
        }
    }

    /// <summary>
    /// <c>-- determinism file.sprt runs [Name=expr ...]</c>: rebuilds the model several times (equations set as
    /// given) and fingerprints every feature's bodies (face, edge and vertex counts, face names, a sum of the
    /// vertices, edge curves, face surfaces and anchors, edge sample caches); prints the features whose result differs
    /// between runs. FC_DET_FACES=1 also tessellates the final body as the demo check measures volume (2e-4, slow) and
    /// prints the faces whose tessellated volume differs. 0 when every run matched.
    /// </summary>
    public static int Determinism(string file, int runs, string[] equations)
    {
        var json = CadDocument.Load(file).ToJson();
        var prints = new List<List<string>>();
        var faceVols = new List<Dictionary<string, (double vol, int n)>>();
        List<string> names = new();
        for (int run = 0; run < runs; run++)
        {
            var doc = CadDocument.FromJson(json);
            foreach (var eq in equations)
            {
                var kv = eq.Split('=', 2);
                doc.Equations.First(q => q.Name == kv[0]).Expr = kv[1];
            }
            Csg.ClearCache();
            var r = new Regenerator().Rebuild(doc);
            names = doc.Features.Select(f => f.Name).ToList();
            var fp = r.States.Select(Fingerprint).ToList();
            if (Environment.GetEnvironmentVariable("FC_DET_FACES") != null)
            {
                // the final body tessellated as the demo check measures volume, per face
                var facets = NTess.Facets(r.State.Bodies[0].Solid.Native!, 2e-4);
                // faces sharing a name: told apart by their order of appearance
                var tagKey = new Dictionary<FaceTag, string>(ReferenceEqualityComparer.Instance);
                var perName = new Dictionary<string, int>();
                foreach (var f in facets)
                    if (!tagKey.ContainsKey(f.Tag)) { perName[f.Tag.Name] = perName.GetValueOrDefault(f.Tag.Name) + 1; tagKey[f.Tag] = $"{f.Tag.Name}#{perName[f.Tag.Name]}"; }
                faceVols.Add(facets.GroupBy(f => tagKey[f.Tag]).ToDictionary(g => g.Key, g => (g.Sum(FacetVolume), g.Count())));
                Console.Write($"[{faceVols[^1].Values.Sum(v => v.Item1):R}]");
            }
            prints.Add(fp);
            Console.Write(".");
        }
        Console.WriteLine();
        int bad = 0;
        for (int k = 0; k < names.Count; k++)
        {
            var distinct = prints.Select(p => p[k]).Distinct().ToList();
            if (distinct.Count == 1) continue;
            bad++;
            Console.WriteLine($"{names[k]}: {distinct.Count} different results in {runs} runs");
            foreach (var d in distinct) Console.WriteLine($"    x{prints.Count(p => p[k] == d)}  {d}");
        }
        foreach (var key in faceVols.SelectMany(d => d.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            var vals = faceVols.Select(d => d.TryGetValue(key, out var v) ? $"{v.vol:R}/{v.n}" : "missing").ToList();
            if (vals.Distinct().Count() > 1) Console.WriteLine($"  face {key}: {string.Join("  ", vals.GroupBy(v => v).Select(g => $"x{g.Count()} {g.Key}"))}");
        }
        // per feature, a short hash of its result (compare between processes)
        for (int k = 0; k < names.Count; k++) Console.WriteLine($"  {names[k],-20} {(uint)StableHash(prints[0][k]):X8}");
        Console.WriteLine(bad == 0 ? "every run matched" : $"{bad} features differ");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>-- demoprints name</c>: the demo check's sequence for one demo (built in code, each variant in turn, one
    /// process), printing a hash of every variant's final body: compare the output of several processes.
    /// </summary>
    public static int DemoPrints(string name)
    {
        foreach (var (file, build) in Demos.All.Where(x => x.file.Contains(name)))
        {
            var doc = build();
            foreach (var variant in new[] { Array.Empty<(string, string)>() }.Concat(Demos.Variants[file]))
            {
                var d = CadDocument.FromJson(doc.ToJson());
                foreach (var (n, e) in variant) d.Equations.First(q => q.Name == n).Expr = e;
                Csg.ClearCache();
                var r = new Regenerator().Rebuild(d);
                var label = variant.Length == 0 ? "as designed" : string.Join(", ", variant.Select(v => $"{v.Item1}={v.Item2}"));
                Console.WriteLine($"{file} {label}");
                for (int k = 0; k < d.Features.Count; k++) Console.WriteLine($"  {d.Features[k].Name,-20} {(uint)StableHash(Fingerprint(r.States[k])):X8}");
            }
        }
        return 0;
    }

    /// <summary>
    /// <c>-- massprops file.sprt [Name=expr ...]</c>: exact mass properties of each body (integrated on the B-rep)
    /// against the mesh's (display mesh, and finer tessellations), with times.
    /// </summary>
    public static int MassPropsTool(string file, string[] equations)
    {
        var doc = CadDocument.Load(file);
        foreach (var eq in equations) { var kv = eq.Split('=', 2); doc.Equations.First(q => q.Name == kv[0]).Expr = kv[1]; }
        var r = new Regenerator().Rebuild(doc);
        foreach (var b in r.State.Bodies)
        {
            if (b.Solid.Native is not { } nb) { Console.WriteLine("  (not a native body)"); continue; }
            var sw = Stopwatch.StartNew();
            var ex = NMass.Of(nb);
            var ms = sw.Elapsed.TotalMilliseconds;
            if (ex == null) { Console.WriteLine($"  exact: not integrable ({ms:0} ms)"); continue; }
            Console.WriteLine($"  exact      vol {ex.Volume:R}  area {ex.SurfaceArea:0.000000}  centre {ex.Center}  Ixx {ex.AtCenter[0, 0]:0.###}  ({ms:0} ms, {nb.Faces.Count} faces)");
            foreach (var tol in new[] { 0.0, 1e-3, 2e-4 })
            {
                sw.Restart();
                var mp = new MassProps();
                if (tol == 0) mp.Add(b.Mesh.Positions, b.Mesh.Indices);
                else foreach (var f in NTess.Facets(nb.Clone(), tol)) foreach (var tr in Triangulator.TriangulateFace(f)) mp.AddTriangle(tr.A, tr.B, tr.C);
                mp.Finish();
                Console.WriteLine($"  mesh {(tol == 0 ? "display" : tol.ToString("0e0")),-7} vol {mp.Volume:R}  area {mp.SurfaceArea:0.000000}  rel {(mp.Volume - ex.Volume) / ex.Volume:e2}  ({sw.ElapsedMilliseconds} ms)");
            }
        }
        return 0;
    }

    /// <summary>
    /// <c>-- tessrepeat file.sprt n [Name=expr ...]</c>: rebuilds once, then tessellates the final body n times (as
    /// the demo check measures volume): a race inside tessellation shows up as different volumes.
    /// </summary>
    public static int TessRepeat(string file, int n, string[] equations, bool fresh)
    {
        var doc = CadDocument.Load(file);
        foreach (var eq in equations) { var kv = eq.Split('=', 2); doc.Equations.First(q => q.Name == kv[0]).Expr = kv[1]; }
        var r = new Regenerator().Rebuild(doc);
        var body = r.State.Bodies[0].Solid.Native!;
        var seen = new Dictionary<double, int>();
        for (int k = 0; k < n; k++)
        {
            var sw = Stopwatch.StartNew();
            var v = NTess.ToSolid(fresh ? body.Clone() : body, 2e-4).Volume();
            seen[v] = seen.GetValueOrDefault(v) + 1;
            Console.WriteLine($"{k}: {v:R}  ({sw.ElapsedMilliseconds} ms)");
        }
        Console.WriteLine(seen.Count == 1 ? "all the same" : $"{seen.Count} different volumes");
        return seen.Count == 1 ? 0 : 1;
    }

    internal static string Fingerprint(ModelState s)
    {
        var parts = new List<string>();
        foreach (var b in s.Bodies)
        {
            if (b.Solid.Native is not { } nb) { parts.Add("facets"); continue; }
            var verts = nb.Faces.SelectMany(f => f.Coedges).Select(c => c.Edge.V0).Distinct().ToList();
            double sx = 0, sy = 0, sz = 0;
            foreach (var v in verts) { sx += v.P.X; sy += v.P.Y; sz += v.P.Z; }
            var faceNames = string.Join(",", nb.Faces.Select(f => f.Tag.Name).OrderBy(n => n, StringComparer.Ordinal));
            var tols = string.Join(",", nb.Edges.Select(e => e.SampledTol).GroupBy(t => t).OrderBy(g => g.Key).Select(g => $"{g.Key:g3}x{g.Count()}"))
                + $" order#{(uint)StableHash(string.Join(",", nb.Edges.Select(e => e.SampledTol.ToString("R")))):X8}"
                + $" points#{PointsHash(nb):X16} curves#{CurvesHash(nb):X16} faces#{FacesHash(nb):X16}";
            parts.Add($"{nb.Faces.Count}f {nb.Edges.Count()}e {verts.Count}v sum({sx:0.######},{sy:0.######},{sz:0.######}) names#{faceNames.Length}:{(uint)StableHash(faceNames):X8} samples[{tols}]");
        }
        return string.Join(" | ", parts);
    }

    static double FacetVolume(Face f)
    {
        double v = 0;
        foreach (var l in f.Loops)
            for (int i = 1; i + 1 < l.Length; i++) v += Vec3.Dot(l[0], Vec3.Cross(l[i], l[i + 1]));
        return v / 6;
    }

    // every edge's curve evaluated along it (not from caches): fitted section curves can differ where no vertex moves
    static ulong CurvesHash(NBody nb)
    {
        ulong h = 14695981039346656037;
        foreach (var e in nb.Edges)
            for (int k = 0; k <= 8; k++)
            {
                var q = e.Curve.Eval(e.T0 + (e.T1 - e.T0) * k / 8);
                foreach (var x in new[] { q.X, q.Y, q.Z }) h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(x)) * 1099511628211;
            }
        return h;
    }

    // what tessellation reads from each face besides its edges: anchor, sense, the surface at a few parameters
    static ulong FacesHash(NBody nb)
    {
        ulong h = 14695981039346656037;
        void H(double x) => h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(x)) * 1099511628211;
        foreach (var f in nb.Faces)
        {
            H(f.Sense ? 1 : 0);
            if (f.Anchor is { } an) { H(an.X); H(an.Y); H(an.Z); } else H(-1);
            for (int k = 0; k < 3; k++) { var q = f.Surface.Eval(0.1 + 0.3 * k, 0.2 + 0.25 * k); H(q.X); H(q.Y); H(q.Z); }
        }
        return h;
    }

    static ulong PointsHash(NBody nb)
    {
        ulong h = 14695981039346656037;
        foreach (var e in nb.Edges)
        {
            if (double.IsNaN(e.SampledTol)) continue;
            foreach (var q in e.Samples(e.SampledTol))
                foreach (var x in new[] { q.X, q.Y, q.Z }) h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(x)) * 1099511628211;
        }
        return h;
    }

        static int StableHash(string s) { unchecked { int h = 17; foreach (var c in s) h = h * 31 + c; return h; } }

    /// <summary>
    /// <c>-- cases</c>: the handwheel at awkward parameters (off-grid spokes; the grips-last model with 8 spokes):
    /// per case the time, volume, errors and warnings, one line each (compare two builds by diffing the output).
    /// </summary>
    public static void Cases(string root)
    {
        var cur = Path.Combine(root, "samples", "demos", "spoked-handwheel.sprt");
        var old = Path.Combine(root, "tests", "SpryCad.Tests", "models", "spoked-handwheel-grips-last.sprt");
        var cases = new List<(string name, string file, Action<CadDocument> edit)>();
        foreach (var a in new[] { "90", "150", "210", "230", "250", "270", "300", "340" })
            cases.Add(($"spokes {a}", cur, d => d.Features.OfType<CircularPatternFeature>().First(f => f.Name == "Spokes").Angle = a));
        cases.Add(("old NS=8", old, d => d.Equations.First(e => e.Name == "NS").Expr = "8"));
        foreach (var a in new[] { "226", "236", "250", "300" })
            cases.Add(($"old spokes {a}", old, d => d.Features.OfType<CircularPatternFeature>().First(f => f.Name == "Spokes").Angle = a));
        // a local working part, when present (not in the repository)
        var local = Path.Combine(root, "samples", "demos", "spoked-handwheel-FilletAfter.sprt");
        if (File.Exists(local)) cases.Add(("fillet after", local, _ => { }));
        foreach (var (name, file, edit) in cases)
        {
            var doc = CadDocument.Load(file);
            edit(doc);
            Csg.ClearCache();
            var sw = Stopwatch.StartNew();
            var r = new Regenerator().Rebuild(doc);
            var ms = sw.Elapsed.TotalMilliseconds;
            var errs = doc.Features.Where(f => r.Status[f.Id].Error != null).Select(f => f.Name).ToList();
            int warns = doc.Features.Sum(f => r.Status[f.Id].Warnings.Count);
            var vol = r.State.Bodies.Count > 0 ? r.State.Bodies.Sum(b => b.Solid.Volume()) : 0;
            Console.WriteLine($"{name,-18} {ms,8:0} ms  vol {vol,10:0}  warnings {warns,2}  errors [{string.Join(", ", errs)}]");
            if (Environment.GetEnvironmentVariable("FC_CASES_STEP") is { } outDir && r.State.Bodies.Count > 0)
            {
                Directory.CreateDirectory(outDir);
                using var fs = File.Create(Path.Combine(outDir, name.Replace(" ", "_").Replace("=", "") + ".step"));
                SpryCad.Kernel.StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), name);
            }
        }
    }

    /// <summary><c>-- markertime file.sprt</c>: what showing a pattern's instance markers costs (the app does it on the UI thread).</summary>
    public static void MarkerTime(string file)
    {
        var doc = CadDocument.Load(file);
        var r = new Regenerator().Rebuild(doc);
        Func<string, double?> lookup = n => r.EquationValues.TryGetValue(n, out var v) ? v : null;
        for (int i = 0; i < doc.Features.Count; i++)
        {
            if (doc.Features[i] is not (CircularPatternFeature or LinearPatternFeature)) continue;
            var f = doc.Features[i];
            var st = r.States[i - 1];
            var seeds = f is CircularPatternFeature cp ? cp.Seeds : ((LinearPatternFeature)f).Seeds;
            var sw = Stopwatch.StartNew();
            var box = Box3.Empty;
            foreach (var id in seeds) if (st.Tools.TryGetValue(id, out var t)) box.Add(t.Tool.Bounds);
            var t1 = sw.Elapsed.TotalMilliseconds;
            var ts = Regenerator.PatternTransforms(f, st, lookup);
            Console.WriteLine($"{f.Name}: seed tools {t1:0.0} ms, transforms {sw.Elapsed.TotalMilliseconds - t1:0.0} ms ({ts!.Count} instances)");
        }
    }

    static double Median(List<double> l) { var s = l.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
}

/// <summary>
/// <c>FC_ALLOC=1</c>: samples allocations (the runtime's AllocationTick, about one event per 100 KB) by type
/// during the last benchmark run.
/// </summary>
sealed class AllocSampler : System.Diagnostics.Tracing.EventListener
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _bytes = new();
    bool _on = true;

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (!_on || e.EventName == null || !e.EventName.StartsWith("GCAllocationTick") || e.Payload == null) return;
        int ti = e.PayloadNames!.IndexOf("TypeName"), ai = e.PayloadNames.IndexOf("AllocationAmount64");
        if (ti < 0) return;
        var name = e.Payload[ti] as string ?? "?";
        long amount = ai >= 0 ? Convert.ToInt64(e.Payload[ai]) : 100_000;
        _bytes.AddOrUpdate(name, amount, (_, v) => v + amount);
    }

    public void Print(int top)
    {
        _on = false;
        var total = _bytes.Values.Sum();
        Console.WriteLine($"  allocations sampled: {total / 1e6:0} MB");
        foreach (var (name, b) in _bytes.OrderByDescending(x => x.Value).Take(top))
            Console.WriteLine($"    {b / 1e6,8:0.0} MB  {name}");
    }
}
