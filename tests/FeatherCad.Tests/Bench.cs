using System.Diagnostics;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

/// <summary>Rebuild benchmark: <c>dotnet run -c Release -- bench file.fcad [runs]</c>.</summary>
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
            var sw = Stopwatch.StartNew();
            var r = new Regenerator().Rebuild(doc);
            var ms = sw.Elapsed.TotalMilliseconds;
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
    static System.Reflection.PropertyInfo? NumericParam(FeatherCad.Core.Model.Feature f)
        => f.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.Name is not ("Name" or "Id"))
            .FirstOrDefault(p => p.GetValue(f) is string v && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0);

    /// <summary>
    /// <c>-- repro file.fcad FeatureName Prop value</c>: rebuilds with one parameter changed and the boolean trace
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
        FeatherCad.Kernel.Native.NBoolean.Trace = m => log.Add(m);
        int before = Csg.NativeFallback;
        bool prof = Environment.GetEnvironmentVariable("FC_PROF") != null;
        if (prof) { Prof.Reset(); Prof.Enabled = true; }
        var r = new Regenerator().Rebuild(doc);
        if (prof)
        {
            Prof.Enabled = false;
            foreach (var (name, pms, calls) in Prof.Results().Take(30)) Console.WriteLine($"      {name,-24} {pms,8:0.0} ms  x{calls}");
        }
        FeatherCad.Kernel.Native.NBoolean.Trace = null;
        foreach (var ff in doc.Features) Console.WriteLine($"  {ff.Name,-16} {r.Status[ff.Id].Milliseconds,8:0} ms {r.Status[ff.Id].Error} {string.Join("; ", r.Status[ff.Id].Warnings)}");
        Console.WriteLine($"fallbacks: {Csg.NativeFallback - before}, last: {Csg.LastFallbackReason}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "fc-repro-trace.txt"), log);
        Console.WriteLine($"trace: {log.Count} lines -> %TEMP%/fc-repro-trace.txt");
    }

    /// <summary>
    /// <c>-- featprof file.fcad Feature [runs]</c>: the time of one feature (everything before it cached), with the
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

    static double Median(List<double> l) { var s = l.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
}
