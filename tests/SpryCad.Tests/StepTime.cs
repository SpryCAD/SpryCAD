using System.Diagnostics;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary><c>-- steptime file.sprt [runs]</c>: how long exporting a part to STEP takes, stage by stage.</summary>
static class StepTime
{
    public static int Run(string file, int runs)
    {
        var doc = CadDocument.Load(file);
        if (Environment.GetEnvironmentVariable("STEP_COSMETIC") != null)
        {
            // a part shown with cosmetic threads, exported (threads modeled): rebuilt cold as before, seeded from the
            // display's results, and once more (nothing changed)
            var display = new Regenerator { ThreadOverride = ThreadDisplay.Cosmetic };
            display.Rebuild(doc);
            double Ms(Func<RegenResult> f, out double vol) { var w = Stopwatch.StartNew(); var res = f(); var t = w.Elapsed.TotalMilliseconds; vol = res.State.Bodies.Sum(b => SpryCad.Kernel.Native.NMass.Of(b.Solid.Native!)?.Volume ?? 0); return t; }
            Csg.ClearCache();
            var cold = Ms(() => new Regenerator { ThreadOverride = ThreadDisplay.Modeled }.Rebuild(doc.Clone()), out var v0);
            Csg.ClearCache();
            var export = new Regenerator { ThreadOverride = ThreadDisplay.Modeled };
            var seeded = Ms(() => { export.SeedFrom(display); return export.Rebuild(doc.Clone()); }, out var v1);
            var again = Ms(() => { export.SeedFrom(display); return export.Rebuild(doc.Clone()); }, out var v2);
            Console.WriteLine($"{Path.GetFileName(file)} export rebuild: cold {cold:0} ms, seeded {seeded:0} ms, again {again:0} ms; volumes {(v0 == v1 && v1 == v2 ? "identical" : $"DIFFER {v0:R} {v1:R} {v2:R}")}");
            return 0;
        }
        var r = new Regenerator().Rebuild(doc);
        var bodies = r.State.Bodies.Select(b => (b.Id, b.Solid)).ToList();
        var times = new List<double>(); long bytes = 0;
        for (int i = 0; i <= runs; i++)
        {
            Prof.Reset(); Prof.Enabled = i == runs;
            var ms = new MemoryStream();
            var sw = Stopwatch.StartNew();
            StepWriter.Write(ms, bodies, Path.GetFileNameWithoutExtension(file));
            if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds);
            bytes = ms.Length;
        }
        times.Sort();
        Console.WriteLine($"{Path.GetFileName(file)}: {bodies.Sum(b => b.Solid.Native?.Faces.Count ?? 0)} faces, {bytes / 1024} KB, median {times[times.Count / 2]:0.0} ms (first {times[0]:0.0})");
        foreach (var (n, ms2, calls) in Prof.Results().Take(12)) Console.WriteLine($"    {n,-26} {ms2,8:0.0} ms x{calls}");
        return 0;
    }
}
