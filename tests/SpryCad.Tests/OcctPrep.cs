using System.Diagnostics;
using System.Globalization;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>
/// <c>-- occtprep file.sprt Pattern outdir [runs]</c>: a pattern feature's work, for comparing kernels. Writes the body
/// before the pattern (body.step), the seed's tool (tool.step) and the instances' motions (transforms.txt: twelve
/// numbers a line, the rows of the 3x4 matrix), then times SpryCAD doing the same: the tool moved to every instance,
/// the copies fused, cut from (or added to) the body, the boolean cache cleared each run. Prints the result's exact
/// volume and face count; with FC_OCCT_STEP=1 also on the geometry read back from the STEP files (as the other kernel gets it).
/// </summary>
static class OcctPrep
{
    public static int Run(string file, string name, string outDir, int runs)
    {
        var doc = CadDocument.Load(file);
        int idx = doc.Features.FindLastIndex(f => f.Name == name);
        if (idx < 0) { Console.WriteLine($"no feature {name}"); return 1; }
        var pat = doc.Features[idx];
        var seeds = pat switch { CircularPatternFeature c => c.Seeds, LinearPatternFeature l => l.Seeds, _ => throw new Exception("not a pattern") };
        var st = new Regenerator().Rebuild(doc, idx).State;
        var body = st.Bodies.Single().Solid;
        var info = st.Tools[seeds.Single()];
        var tool = info.Tool;
        var (values, _) = Regenerator.EvaluateEquations(doc);
        var transforms = Regenerator.PatternTransforms(pat, st, n => values.TryGetValue(n, out var v) ? v : null)!;
        Directory.CreateDirectory(outDir);
        using (var fs = File.Create(Path.Combine(outDir, "body.step"))) StepWriter.Write(fs, new[] { ("body", body) }, "body");
        using (var fs = File.Create(Path.Combine(outDir, "tool.step"))) StepWriter.Write(fs, new[] { ("tool", tool) }, "tool");
        var ci = CultureInfo.InvariantCulture;
        File.WriteAllLines(Path.Combine(outDir, "transforms.txt"), transforms.Select(t => string.Join(" ",
            new[] { t.M00, t.M01, t.M02, t.T.X, t.M10, t.M11, t.M12, t.T.Y, t.M20, t.M21, t.M22, t.T.Z }.Select(x => x.ToString("R", ci)))));
        File.WriteAllText(Path.Combine(outDir, "op.txt"), info.IsCut ? "cut" : "fuse");
        Console.WriteLine($"{name}: {transforms.Count} copies of a {tool.Native!.Faces.Count}-face tool, {(info.IsCut ? "cut from" : "added to")} a {body.Native!.Faces.Count}-face body");

        void Time(string label, Solid b0, Solid t0)
        {
            var times = new List<double>();
            Solid? result = null;
            for (int run = 0; run <= runs; run++)
            {
                Csg.ClearCache();
                var sw = Stopwatch.StartNew();
                var copies = transforms.Select((t, i) => t0.Transformed(t, $"P.{i + 1}.")).ToList();
                var all = Csg.UnionAll(copies);
                result = info.IsCut ? Csg.Subtract(b0, all) : Csg.Union(b0, all);
                if (run > 0) times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            var mp = NMass.Of(result!.Native!);
            var vol = mp?.Volume ?? result.Volume();
            Console.WriteLine($"{label}: median {times[times.Count / 2]:0.0} ms (min {times[0]:0.0}) over {runs} runs; volume {vol:R}{(mp == null ? " (mesh)" : "")}, {result.Native!.Faces.Count} faces");
        }
        Time("SpryCAD", body, tool);
        // on the geometry the STEP files hold (what another kernel reads: e.g. thread faces as B-splines), when asked
        // (FC_OCCT_STEP=1): B-spline threads are far slower than the exact screw surfaces
        if (Environment.GetEnvironmentVariable("FC_OCCT_STEP") == null) return 0;
        Solid Read(string f, string prefix) => NTess.ToSolid(StepImport.Read(File.ReadAllBytes(Path.Combine(outDir, f)), prefix).Solids.Single().Body);
        Time("SpryCAD, on the STEP geometry", Read("body.step", "B."), Read("tool.step", "T."));
        return 0;
    }
}
