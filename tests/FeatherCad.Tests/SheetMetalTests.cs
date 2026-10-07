using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

static class SheetMetalTests
{
    /// <summary>A sheet along an open chain in the XY plane (material on the left), extruded along Z.</summary>
    static NBody Sheet(Vec2[] chain, double t, double r, double width, string prefix = "S")
        => NBuild.Extrude(new[] { new ProfileRegion(SheetProfile.Thicken(chain, t, r)).Normalized() }, Frame.Front, 0, width, prefix);

    static double Area(FlatPattern f) => f.Regions.Sum(r => Math.Abs(r.Outer.SignedArea()) - r.Holes.Sum(h => Math.Abs(h.SignedArea())));

    static double Volume(NBody b) => NTess.ToSolid(b, 0.002).Volume();

    public static void Run(TestRunner t)
    {
        t.Test("Sheet metal: L bracket unfolds to the bend allowance", () =>
        {
            var b = Sheet(new[] { new Vec2(0, 0), new Vec2(50, 0), new Vec2(50, 30) }, 2, 1, 40);
            NativeTests.Check(t, b);
            // straight walls 47 + 27 (the outer bend radius 3 off each), the bend θ (r + K t)
            foreach (var k in new[] { 0.5, 0.3, 0.0 })
            {
                var f = SheetUnfold.Unfold(b, k);
                t.Near(f.Thickness, 2, 1e-9);
                t.Eq(f.Bends.Count, 1);
                t.Near(f.Bends[0].Angle, Math.PI / 2, 1e-9);
                t.Near(f.Bends[0].InnerRadius, 1, 1e-9);
                var len = 74 + Math.PI / 2 * (1 + k * 2);
                t.Near(Area(f), len * 40, 1e-6);
                NativeTests.Check(t, f.Body, len * 40 * 2, 1e-6);
                // the bend line runs across the strip
                t.Near(Vec2.Distance(f.Bends[0].A, f.Bends[0].B), 40, 1e-9);
            }
            // K = 0.5: the neutral fiber is the middle, the flat has the folded volume (walls 47·2 + 27·2, bend π/4 (3² - 1²))
            t.Near(Volume(SheetUnfold.Unfold(b, 0.5).Body), 40 * (148 + 2 * Math.PI), 1e-6);
        });

        t.Test("Sheet metal: U channel with a hole keeps the hole round", () =>
        {
            var b = Sheet(new[] { new Vec2(0, 30), new Vec2(0, 0), new Vec2(60, 0), new Vec2(60, 30) }, 2, 1, 40);
            var hole = NativeTests.Cyl(new Vec3(30, -5, 20), Vec3.UnitY, 5, 20, "H");
            b = NBoolean.Run(b, hole, BoolOp.Subtract);
            NativeTests.Check(t, b);
            var f = SheetUnfold.Unfold(b, 0.5);
            t.Eq(f.Bends.Count, 2);
            t.True(f.Bends.All(x => x.Up == f.Bends[0].Up), "both bends fold the same way");
            t.Eq(f.Regions.Count, 1);
            t.Eq(f.Regions[0].Holes.Count, 1);
            var len = 27 + 54 + 27 + 2 * Math.PI / 2 * 2;
            t.Near(Math.Abs(f.Regions[0].Outer.SignedArea()), len * 40, 1e-6);
            NativeTests.Check(t, f.Body);
            t.Near(Volume(f.Body), 2 * (len * 40 - Math.PI * 25), 0.2); // the tessellated hole is a little small
            // the hole is an exact circle in the flat body and in the DXF
            t.True(f.Body.Faces.Any(x => x.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(x.Surface.R - 5) < 1e-9), "round hole");
            using var ms = new MemoryStream();
            DxfWriter.Write(ms, f);
            var dxf = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            t.True(dxf.Contains("CIRCLE"), "DXF circle");
            t.Eq(dxf.Split("BEND_").Length - 1, 2);
        });

        t.Test("Sheet metal: shelled box with filleted bends unfolds", () =>
        {
            // a box shelled open at the top and both ends is a U channel with sharp corners; fillets r and r + t
            // on the inside and outside of each bottom corner make them bends
            var box = NativeTests.Box(Vec3.Zero, new Vec3(100, 60, 40));
            box.Link();
            NFace FaceAt(NBody body, Vec3 n, Func<Vec3, bool> where) => body.Faces.First(x =>
            {
                var p = x.Coedges.First().Edge.V0.P;
                var nn = x.Surface.Normal(p) * (x.Sense ? 1 : -1);
                return Vec3.Dot(nn, n) > 1 - 1e-9 && where(p);
            });
            var open = new HashSet<string>
            {
                FaceAt(box, Vec3.UnitZ, p => true).Tag.Name,
                FaceAt(box, Vec3.UnitX, p => true).Tag.Name,
                FaceAt(box, -Vec3.UnitX, p => true).Tag.Name,
            };
            var shelled = NShell.Apply(box, open, 2, "Sh");
            NativeTests.Check(t, shelled);
            var s = NTess.ToSolid(shelled);
            List<NEdge> Corner(Solid sol, double z, double y0, double y1) => sol.Native!.Edges.Where(e =>
                e.Curve is NLine && Math.Abs(e.V0.P.Z - z) < 1e-9 && Math.Abs(e.V1.P.Z - z) < 1e-9
                && (Math.Abs(e.V0.P.Y - y0) < 1e-9 || Math.Abs(e.V0.P.Y - y1) < 1e-9) && Math.Abs(e.V0.P.Y - e.V1.P.Y) < 1e-9).ToList();
            var errors = new List<string>();
            s = NativeBlend.Apply(s, Corner(s, 0, 0, 60), 3, true, "Out", errors);
            t.Eq(string.Join(";", errors), "");
            s = NativeBlend.Apply(s, Corner(s, 2, 2, 58), 1, true, "In", errors);
            t.Eq(string.Join(";", errors), "");
            NativeTests.Check(t, s.Native!);

            var f = SheetUnfold.Unfold(s.Native!, 0.5);
            t.Eq(f.Bends.Count, 2);
            var len = 37 + 54 + 37 + 2 * Math.PI / 2 * 2;
            t.Near(Area(f), len * 100, 1e-6);
            NativeTests.Check(t, f.Body);
            // the folded volume: walls 37·2 twice and 54·2, two bends π/4 (3² - 1²)
            t.Near(Volume(f.Body), 100 * (2 * 37 * 2 + 54 * 2 + 2 * Math.PI / 4 * 8), 1e-6);
        });

        t.Test("Sheet metal: Z bend folds both ways", () =>
        {
            // left turn (outer radius 3 on the chain), then right turn (inner radius 1 on the chain)
            var b = Sheet(new[] { new Vec2(0, 0), new Vec2(40, 0), new Vec2(40, 20), new Vec2(80, 20) }, 2, 1, 30);
            NativeTests.Check(t, b);
            var f = SheetUnfold.Unfold(b, 0.4);
            t.Eq(f.Bends.Count, 2);
            t.True(f.Bends[0].Up != f.Bends[1].Up, "opposite bends");
            var len = 37 + 16 + 39 + 2 * Math.PI / 2 * (1 + 0.4 * 2);
            t.Near(Area(f), len * 30, 1e-6);
            NativeTests.Check(t, f.Body);
        });

        t.Test("Sheet metal: a hole across a bend unrolls", () =>
        {
            var b = Sheet(new[] { new Vec2(0, 30), new Vec2(0, 0), new Vec2(60, 0), new Vec2(60, 30) }, 2, 1, 40);
            // a round hole through the corner, its axis at 45°
            var hole = NativeTests.Cyl(new Vec3(-10, -10, 20), new Vec3(1, 1, 0), 4, 20, "H");
            b = NBoolean.Run(b, hole, BoolOp.Subtract);
            NativeTests.Check(t, b);
            var f = SheetUnfold.Unfold(b, 0.5);
            t.Eq(f.Regions.Count, 1);
            t.Eq(f.Regions[0].Holes.Count, 1);
            t.Eq(f.Bends.Count, 2); // the bend split by the hole is one bend
            NativeTests.Check(t, f.Body);
            var dir = Path.Combine(Path.GetTempPath(), "feathercad-tests");
            Directory.CreateDirectory(dir);
            using (var fs = File.Create(Path.Combine(dir, "flat-bend-hole.dxf"))) DxfWriter.Write(fs, f);
            using (var fs = File.Create(Path.Combine(dir, "flat-bend-hole.step"))) StepWriter.Write(fs, new[] { ("flat", NTess.ToSolid(f.Body)), ("folded", NTess.ToSolid(b)) }, "flat");
        });

        t.Test("Sheet metal: a plain block unfolds to its face", () =>
        {
            // every face has an opposite face but no bend joins them: the flat is the fixed face, as thick as the block
            var f = SheetUnfold.Unfold(NativeTests.Box(Vec3.Zero, new Vec3(30, 20, 10)));
            t.Eq(f.Bends.Count, 0);
            t.Near(f.Thickness, 10, 1e-9);
            t.Near(Area(f), 600, 1e-9);
        });
    }
}
