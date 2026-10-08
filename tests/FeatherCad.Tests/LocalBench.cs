using System.Diagnostics;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

/// <summary>
/// <c>-- localbench file.(step|sprt) [holes]</c>: drills small holes into planar faces of a large part, one boolean
/// each, and reports the time per hole (with FC_PROF=1 the boolean profile). A local boolean costs about the
/// same on a big part as on a small one.
/// </summary>
static class LocalBench
{
    public static int Run(string file, int holes)
    {
        var body = Load(file);
        var diag = body.Bounds.Diagonal;
        Console.WriteLine($"{Path.GetFileName(file)}: {body.Faces.Count} faces, diagonal {diag:0.#}");
        // the largest planar faces, holes at their interior points
        var targets = body.Faces.Where(f => f.Surface.Kind == SurfaceKind.Plane)
            .Select(f => (f, d: NDomain.Of(f))).OrderByDescending(x => Math.Abs(x.d.SignedArea())).Take(holes).ToList();
        var times = new List<double>();
        for (int k = 0; k < targets.Count; k++)
        {
            var (f, d) = targets[k];
            var p = d.InteriorPoint();
            var n = f.Surface.Normal(p) * (f.Sense ? 1 : -1);
            var r = diag / 400;
            var tool = NativeTests.Cyl(p - n * (diag / 100), n, r, diag / 100 + r, "H" + k);
            Prof.Reset(); Prof.Enabled = Environment.GetEnvironmentVariable("FC_PROF") != null;
            var sw = Stopwatch.StartNew();
            try
            {
                var res = NBoolean.Run(body, tool, BoolOp.Subtract);
                times.Add(sw.Elapsed.TotalMilliseconds);
                Console.WriteLine($"  hole {k} in {f.Tag.Name}: {sw.Elapsed.TotalMilliseconds:0.0} ms, {res.Faces.Count} faces");
                body = res;
            }
            catch (Exception ex) { Console.WriteLine($"  hole {k} in {f.Tag.Name}: failed {ex.Message}"); }
            Prof.Enabled = false;
            if (Environment.GetEnvironmentVariable("FC_PROF") != null && (k == targets.Count - 1 || k == 0))
                foreach (var (name, pms, calls) in Prof.Results().Take(60)) Console.WriteLine($"      {name,-24} {pms,8:0.0} ms  x{calls}");
        }
        if (times.Count > 1) Console.WriteLine($"  median {times.Skip(1).OrderBy(x => x).ElementAt((times.Count - 1) / 2):0.0} ms (first {times[0]:0.0})");
        return 0;
    }

    static NBody Load(string file)
    {
        if (file.StartsWith("grid:"))
        {
            // a plate with n x n holes (4 faces per hole), cut in one boolean with a union of disjoint tools
            int n = int.Parse(file[5..]);
            var plate = NativeTests.Box(new Vec3(0, 0, 0), new Vec3(10 * n + 10, 10 * n + 10, 5), "P");
            var tools = new List<NBody>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    tools.Add(NativeTests.Cyl(new Vec3(10 + 10 * i, 10 + 10 * j, -1), new Vec3(0, 0, 1), 2, 7, $"G{i}_{j}"));
            var all = new NBody();
            foreach (var t in tools) all.Faces.AddRange(t.Faces);
            all.Link();
            return NBoolean.Run(plate, all, BoolOp.Subtract);
        }
        if (file.EndsWith(".sprt", StringComparison.OrdinalIgnoreCase))
        {
            var doc = CadDocument.Load(file);
            var r = new Regenerator().Rebuild(doc);
            return r.State.Bodies[0].Solid.Native ?? throw new Exception("not a native body");
        }
        var imp = StepImport.Read(File.ReadAllBytes(file), "F1.");
        return imp.Solids.OrderByDescending(s => s.Body.Faces.Count).First().Body;
    }
}
