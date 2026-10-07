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

/// <summary>The Helical Sweep and Thread features in documents.</summary>
static class ThreadFeatureTests
{
    static FeatherCad.Core.Sketching.Sketch CircleAt(Vec2 c, double r)
    {
        var sk = new FeatherCad.Core.Sketching.Sketch();
        var cp = Vec2.Distance(c, Vec2.Zero) < 1e-12 ? sk.Point(FeatherCad.Core.Sketching.Sketch.OriginId)! : sk.AddPoint(c);
        sk.Add(new FeatherCad.Core.Sketching.SkCircle { Center = cp.Id, R = r });
        return sk;
    }

    // ∫ r dA of a polygon in the (r, z) half plane
    static double FirstMoment(IReadOnlyList<Vec2> p)
    {
        double m = 0;
        for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; m += (a.X + b.X) * (a.X * b.Y - b.X * a.Y); }
        return Math.Abs(m) / 6;
    }

    // the part of a convex polygon on one side of r = R
    static List<Vec2> Clip(IReadOnlyList<Vec2> p, double R, bool inside)
    {
        var res = new List<Vec2>();
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            bool ai = inside ? a.X <= R : a.X >= R, bi = inside ? b.X <= R : b.X >= R;
            if (ai) res.Add(a);
            if (ai != bi) res.Add(a + (b - a) * ((R - a.X) / (b.X - a.X)));
        }
        return res;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Helical Sweep feature: a coil spring from a sketch (circle and centerline), saved and loaded", () =>
        {
            var doc = new FeatherCad.Core.Model.CadDocument();
            var sk = CircleAt(new Vec2(12, 0), 1.5);
            var a = sk.AddPoint(new Vec2(0, -5)); var b = sk.AddPoint(new Vec2(0, 5));
            sk.Add(new FeatherCad.Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
            var s = doc.AddFeature(new FeatherCad.Core.Model.SketchFeature { Plane = FeatherCad.Core.Model.PlaneRef.Front, Sketch = sk });
            var hs = doc.AddFeature(new FeatherCad.Core.Model.HelicalSweepFeature { SketchId = s.Id, Pitch = "4", Revolutions = "5" });
            t.Eq(hs.Name, "Helical1");
            var r = new FeatherCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[hs.Id].Ok, r.Status[hs.Id].Error ?? "");
            double v = 5 * 2 * Math.PI * (Math.PI * 2.25) * 12;
            t.True(Math.Abs(KernelTests.Vex(r.State.Bodies[0].Solid) - v) < 1e-7 * v, "Pappus volume");
            var again = FeatherCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            var r2 = new FeatherCad.Core.Regen.Regenerator().Rebuild(again);
            t.True(Math.Abs(KernelTests.Vex(r2.State.Bodies[0].Solid) - v) < 1e-7 * v, "same after loading");
        });

        t.Test("Thread feature: M10 on a shaft and in a hole (auto size), clearance removes more, saved and loaded", () =>
        {
            foreach (var external in new[] { true, false })
            {
                var doc = new FeatherCad.Core.Model.CadDocument();
                FeatherCad.Core.Model.Feature body;
                double surfaceR;
                if (external)
                {
                    // a 10 mm shaft, 30 long along +Z
                    var s = doc.AddFeature(new FeatherCad.Core.Model.SketchFeature { Plane = FeatherCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(Vec2.Zero, 5) });
                    body = doc.AddFeature(new FeatherCad.Core.Model.ExtrudeFeature { SketchId = s.Id, Depth = "30" });
                    surfaceR = 5;
                }
                else
                {
                    // a 30 × 30 × 30 block with a hole of the M10 minor diameter through it along +Z
                    var s = doc.AddFeature(new FeatherCad.Core.Model.SketchFeature { Plane = FeatherCad.Core.Model.PlaneRef.Front, Sketch = CoreTests.Rect(30, 30) });
                    doc.AddFeature(new FeatherCad.Core.Model.ExtrudeFeature { SketchId = s.Id, Depth = "30" });
                    surfaceR = FeatherCad.Core.Regen.Threads.MinorDiameter(10, 1.5) / 2;
                    var h = doc.AddFeature(new FeatherCad.Core.Model.SketchFeature { Plane = FeatherCad.Core.Model.PlaneRef.Front, Sketch = CircleAt(new Vec2(15, 15), surfaceR) });
                    body = doc.AddFeature(new FeatherCad.Core.Model.ExtrudeFeature { SketchId = h.Id, IsCut = true, End = FeatherCad.Core.Model.EndCondition.ThroughAllBoth });
                }
                var before = new FeatherCad.Core.Regen.Regenerator().Rebuild(doc);
                var cylFace = before.State.Bodies[0].Solid.Native!.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder);
                var axisPoint = external ? Vec3.Zero : new Vec3(15, 15, 0);
                var hint = axisPoint + new Vec3(surfaceR, 0, 28); // near the far end (z = 30)
                double Removed(string clearance)
                {
                    var d = FeatherCad.Core.Model.CadDocument.FromJson(doc.ToJson());
                    var th = d.AddFeature(new FeatherCad.Core.Model.ThreadFeature
                    {
                        Face = new FeatherCad.Core.Model.FaceRef { Name = cylFace.Tag.Name, Hint = hint }, Length = "15", Clearance = clearance,
                    });
                    var r = new FeatherCad.Core.Regen.Regenerator().Rebuild(d);
                    t.True(r.Status[th.Id].Ok, $"{(external ? "external" : "internal")}: {r.Status[th.Id].Error}");
                    t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
                    // saved and loaded, the same
                    var r2 = new FeatherCad.Core.Regen.Regenerator().Rebuild(FeatherCad.Core.Model.CadDocument.FromJson(d.ToJson()));
                    var v = KernelTests.Vex(r.State.Bodies[0].Solid);
                    t.True(Math.Abs(KernelTests.Vex(r2.State.Bodies[0].Solid) - v) < 1e-9 * v, "same after loading");
                    return KernelTests.Vex(before.State.Bodies[0].Solid) - v;
                }
                var removed = Removed("0");
                // the groove's cross-section in the material, swept between 15 mm ± a pitch of thread
                var groove = FeatherCad.Core.Regen.Threads.Groove(external, surfaceR, 5, 1.5, 0).groove;
                var m = FirstMoment(Clip(groove, surfaceR, external));
                double lo = 2 * Math.PI * m * (15 - 1.5) / 1.5, hi = 2 * Math.PI * m * (15 + 1.5) / 1.5;
                t.True(removed > lo && removed < hi, $"{(external ? "external" : "internal")}: removed {removed:0.###} within {lo:0.###}..{hi:0.###}");
                var looser = Removed("0.2");
                t.True(looser > removed + 1, $"clearance removes more: {looser:0.###} vs {removed:0.###}");
            }
        });
    }
}
