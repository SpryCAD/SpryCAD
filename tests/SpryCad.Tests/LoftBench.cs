using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

static class LoftBench
{
    public static int Run()
    {
        Frame At(double z) => new(new Vec3(0, 0, z), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        double cx = (16 * 16 - 10 * 10 - 400) / (2.0 * 6);
        var c = new Vec3(cx, 0, 20); var a = new Vec3(10, 0, 0); var e = new Vec3(10, 0, 40);
        var r = a - c; var axis = Vec3.Cross(r, e - c).Normalized;
        var guide = new List<NSweep.Seg> { new(a, e, c, axis, Math.Acos(Vec3.Dot(r.Normalized, (e - c).Normalized))) };
        var secs = new[] { new NLoft.Section(At(0), new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 10, "C")) }), new NLoft.Section(At(40), new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 10, "C")) }) };
        var sq = new[] { new NLoft.Section(At(0), new[] { new ProfileRegion(ProfileLoop.Polygon(new[] { new Vec2(-10, -10), new Vec2(10, -10), new Vec2(10, 10), new Vec2(-10, 10) }, "L")) }), new NLoft.Section(At(25), new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 8, "C")) }) };
        for (int it = 0; it < 3; it++)
        {
            foreach (var (name, s, g) in new[] { ("guided", secs, new[] { guide }), ("sq-circle", sq, Array.Empty<List<NSweep.Seg>>()) })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var b = NLoft.Loft(s, g, null, "G");
                var t1 = sw.Elapsed.TotalMilliseconds;
                b.Validate();
                var mp = NMass.Of(b);
                var t2 = sw.Elapsed.TotalMilliseconds;
                var sol = NTess.ToSolid(b);
                var t3 = sw.Elapsed.TotalMilliseconds;
                var mesh = sol.Topology;
                var t4 = sw.Elapsed.TotalMilliseconds;
                var box = Csg.Union(sol, Builders.Box(new Vec3(-5, -5, 30), new Vec3(30, 5, 60)));
                var t5 = sw.Elapsed.TotalMilliseconds;
                var bm = box.Topology;
                var t6 = sw.Elapsed.TotalMilliseconds;
                foreach (var f in b.Faces) if (f.Surface.Nurbs is { } nb) Console.WriteLine($"  {f.Tag.Name}: {nb.P.GetLength(0)}x{nb.P.GetLength(1)} poles, spans {nb.SpansU}x{nb.SpansV}, deg {nb.Pu}x{nb.Pv}, minrho {nb.MinCurvatureRadius():0.###}");
                Console.WriteLine($"{name}: loft {t1:0.0} ms, mass {t2 - t1:0.0}, solid {t3 - t2:0.0}, mesh {t4 - t3:0.0}, union {t5 - t4:0.0}, its mesh {t6 - t5:0.0}; faces {b.Faces.Count} native after union {box.Native != null}");
            }
        }
        return 0;
    }
}
