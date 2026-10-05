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
            }
        }
        Prof.Enabled = false;
    }

    static double Median(List<double> l) { var s = l.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
}
