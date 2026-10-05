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
        var json = File.ReadAllText(file);
        Console.WriteLine($"bench {Path.GetFileName(file)}");
        var times = new List<double>();
        var tessTimes = new List<double>();
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
                Console.WriteLine($"  warm full rebuild: median {Median(times):0.0} ms (min {times.Min():0.0}), + display tessellation median {Median(tessTimes):0.0} ms");
                foreach (var (name, l) in perFeature) Console.WriteLine($"    {name,-20} {Median(l),8:0.0} ms");
                Console.WriteLine("  profile (last run):");
                foreach (var (name, pms, calls) in Prof.Results()) Console.WriteLine($"    {name,-24} {pms,8:0.0} ms  x{calls}");
                // incremental: change the last feature only
                var reg = new Regenerator(); reg.Rebuild(doc);
                var last = doc.Features[^1];
                last.Name += " ";
                var sw2 = Stopwatch.StartNew();
                var r2 = reg.Rebuild(doc);
                Console.WriteLine($"  incremental (last feature): {sw2.Elapsed.TotalMilliseconds:0.0} ms, rebuilt {r2.Rebuilt}");
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
                        var sw3 = Stopwatch.StartNew();
                        var r3 = reg.Rebuild(doc);
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
    public static void Repro(string file, string feature, string prop, string value)
    {
        var doc = CadDocument.FromJson(File.ReadAllText(file));
        var f = doc.Features.First(x => x.Name == feature);
        f.GetType().GetProperty(prop)!.SetValue(f, value);
        var log = new List<string>();
        FeatherCad.Kernel.Native.NBoolean.Trace = m => log.Add(m);
        int before = Csg.NativeFallback;
        var r = new Regenerator().Rebuild(doc);
        FeatherCad.Kernel.Native.NBoolean.Trace = null;
        foreach (var ff in doc.Features) Console.WriteLine($"  {ff.Name,-16} {r.Status[ff.Id].Milliseconds,8:0} ms {r.Status[ff.Id].Error}");
        Console.WriteLine($"fallbacks: {Csg.NativeFallback - before}, last: {Csg.LastFallbackReason}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "fc-repro-trace.txt"), log);
        Console.WriteLine($"trace: {log.Count} lines -> %TEMP%/fc-repro-trace.txt");
    }

    static double Median(List<double> l) { var s = l.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
}
