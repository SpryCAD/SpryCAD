using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

/// <summary>Helical sweeps: exact screw surfaces; volumes by Pappus for a screw motion (angle × first moment of the profile about the axis).</summary>
static class HelicalTests
{
    // ∫ r dA of a polygon in the (r, h) half plane: (1/6) Σ (r_i + r_j)(r_i h_j − r_j h_i)
    static double FirstMoment(IReadOnlyList<Vec2> p)
    {
        double m = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            m += (a.X + b.X) * (a.X * b.Y - b.X * a.Y);
        }
        return Math.Abs(m) / 6;
    }

    // the part of a convex polygon with r ≤ R
    static List<Vec2> ClipInside(IReadOnlyList<Vec2> p, double R)
    {
        var res = new List<Vec2>();
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            bool ai = a.X <= R, bi = b.X <= R;
            if (ai) res.Add(a);
            if (ai != bi) res.Add(a + (b - a) * ((R - a.X) / (b.X - a.X)));
        }
        return res;
    }

    static double Volume(TestRunner t, NBody b)
    {
        t.Eq(b.Validate(), null);
        var mp = NMass.Of(b);
        t.True(mp != null, "integrable");
        var tess = NTess.ToSolid(b);
        t.Eq(tess.Topology.OpenHalfEdges, 0);
        return mp!.Volume;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Helical sweep: a coil spring (circle profile, 3 turns) has the Pappus volume; mirrored and left-handed alike", () =>
        {
            // the profile in the XY plane, a circle r 2 at x = 10; the helix about the Y axis, lead 6
            var coil = new ProfileRegion(ProfileLoop.Circle(new Vec2(10, 0), 2, "C"));
            var b = NHelical.Sweep(new[] { coil }, Frame.Front, Vec3.Zero, Vec3.UnitY, 6, 3 * 2 * Math.PI, "S");
            double v = 3 * 2 * Math.PI * (Math.PI * 4) * 10;
            var got = Volume(t, b);
            t.True(Math.Abs(got - v) < 1e-7 * v, $"volume {got:R} vs {v:R}");
            t.True(b.Faces.Any(f => f.Surface.Kind == SurfaceKind.Screw), "screw faces");
            // left hand (negative lead) and a mirrored copy: the same volume
            var lh = NHelical.Sweep(new[] { coil }, Frame.Front, Vec3.Zero, Vec3.UnitY, -6, 3 * 2 * Math.PI, "L");
            t.True(Math.Abs(Volume(t, lh) - v) < 1e-7 * v, "left hand");
            var mir = b.Transformed(Transform.Mirror(Plane.FromPointNormal(Vec3.Zero, Vec3.UnitX)));
            t.True(Math.Abs(Volume(t, mir) - v) < 1e-7 * v, "mirrored");
            // a profile taller than the lead is refused
            bool refused = false;
            try { NHelical.Sweep(new[] { coil }, Frame.Front, Vec3.Zero, Vec3.UnitY, 3, 2 * 2 * Math.PI, "X"); } catch (ArgumentException) { refused = true; }
            t.True(refused, "overlapping turns refused");
        });

        t.Test("Helical sweep: a 60° thread cut into a cylinder removes angle × the first moment of the part inside", () =>
        {
            // cylinder r 10 along Y from y = 0 to 30
            var frame = new Frame(Vec3.Zero, Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
            var cyl = NBuild.Extrude(new[] { new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 10, "C")) }, frame, 0, 30, "Shaft");
            // a 60° V, pitch 3: apex at r 8.4, flanks reaching r 10.9 (past the surface), centred at y = 6
            double pitch = 3, apexR = 8.4, outR = 10.9, h = (outR - apexR) * Math.Tan(Math.PI / 6);
            var tri = new[] { new Vec2(apexR, 6), new Vec2(outR, 6 - h), new Vec2(outR, 6 + h) };
            var tool = NHelical.Sweep(new[] { new ProfileRegion(ProfileLoop.Polygon(tri, "T")) }, Frame.Front, Vec3.Zero, Vec3.UnitY, pitch, 4 * 2 * Math.PI, "Th");
            t.True(Math.Abs(Volume(t, tool) - 4 * 2 * Math.PI * FirstMoment(tri)) < 1e-7 * Volume(t, tool), "tool volume");
            var cut = NBoolean.Run(cyl, tool, BoolOp.Subtract);
            var removed = 4 * 2 * Math.PI * FirstMoment(ClipInside(tri, 10));
            var expect = Math.PI * 100 * 30 - removed;
            var got = Volume(t, cut);
            t.True(Math.Abs(got - expect) < 1e-6 * expect, $"threaded volume {got:R} vs {expect:R} (removed {removed:R})");
            // the groove meets the cylinder along helices
            t.True(cut.Edges.Any(e => e.Curve is NHelix), "helical edges");
        });
    }
}
