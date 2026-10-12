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
