using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>Fillets fading out: the variable-radius rolling ball (<see cref="RollingBall.Fade"/>).</summary>
static class FadeTests
{
    public static void Run(TestRunner t)
    {
        // removed (convex) or added (concave) along a straight edge between two square planes: the section's area
        // (1 − π/4) ρ², ρ = r (1 − 3u² + 2u³), integrated: (1 − π/4) r² L · 13/35
        const double r = 5, L = 100;
        var expect = (1 - Math.PI / 4) * r * r * L * 13 / 35;

        t.Test("Fade: a fillet fading out along a block's edge, cut away", () =>
        {
            var box = Builders.Box(Vec3.Zero, new Vec3(L, 50, 20));
            var e = box.Native!.Edges.Single(x => Vec3.Distance(x.Mid, new Vec3(L / 2, 0, 20)) < 1e-9);
            var (ta, tb) = Ends(e, new Vec3(0, 0, 20));
            var rb = RollingBall.Fade(e, ta, tb, r, "F", (f, p) => true);
            t.True(rb.Error == null, rb.Error ?? "");
            t.True(rb.Convex, "convex");
            var res = Csg.Subtract(box, rb.Tool!);
            t.True(res.Native != null && res.Native.Validate() == null, "valid");
            var removed = NMass.Of(box.Native!)!.Volume - NMass.Of(res.Native!)!.Volume;
            t.True(Math.Abs(removed - expect) < 2e-3 * expect, $"removed {removed:g6}, expected {expect:g6}");
            t.True(res.Topology.OpenHalfEdges == 0, "closed mesh");
        });

        t.Test("Fade: a fillet fading out along an inside corner, filled", () =>
        {
            // an L: the inside corner along x at y = 20, z = 20
            var a = Builders.Box(Vec3.Zero, new Vec3(L, 50, 20));
            var b = Builders.Box(new Vec3(0, 0, 20), new Vec3(L, 20, 50));
            var body = Csg.Union(a, b);
            var e = body.Native!.Edges.Single(x => Vec3.Distance(x.Mid, new Vec3(L / 2, 20, 20)) < 1e-9);
            var (ta, tb) = Ends(e, new Vec3(0, 20, 20));
            var rb = RollingBall.Fade(e, ta, tb, r, "F", (f, p) => true);
            t.True(rb.Error == null, rb.Error ?? "");
            t.True(!rb.Convex, "concave");
            var res = Csg.Union(body, rb.Tool!);
            t.True(res.Native != null && res.Native.Validate() == null, "valid");
            var added = NMass.Of(res.Native!)!.Volume - NMass.Of(body.Native!)!.Volume;
            t.True(Math.Abs(added - expect) < 2e-3 * expect, $"added {added:g6}, expected {expect:g6}");
        });

        // (the parameter at the end nearest start first)
        static (double, double) Ends(NEdge e, Vec3 start)
            => Vec3.Distance(e.V0.P, start) < Vec3.Distance(e.V1.P, start) ? (e.T0, e.T1) : (e.T1, e.T0);
    }
}
