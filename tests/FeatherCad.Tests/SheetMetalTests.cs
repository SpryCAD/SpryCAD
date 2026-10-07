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

        t.Test("Sheet metal: Flat Pattern feature on a shelled, filleted part", () =>
        {
            // box 100 x 60 x 40 → shell open at the top and both ends (a U channel) → bends by fillets → flat
            var doc = new Core.Model.CadDocument();
            var s1 = doc.AddFeature(new Core.Model.SketchFeature { Plane = Core.Model.PlaneRef.Front, Sketch = CoreTests.Rect(100, 60) });
            doc.AddFeature(new Core.Model.ExtrudeFeature { SketchId = s1.Id, Depth = "40" });
            Core.Model.FaceRef Face(Vec3 hint, Vec3 n) => new() { Hint = hint, Normal = n };
            doc.AddFeature(new Core.Model.ShellFeature
            {
                Thickness = "2",
                Faces = { Face(new Vec3(50, 30, 40), Vec3.UnitZ), Face(new Vec3(0, 30, 20), -Vec3.UnitX), Face(new Vec3(100, 30, 20), Vec3.UnitX) },
            });
            var regen = new Core.Regen.Regenerator();
            var r = regen.Rebuild(doc);
            Core.Model.EdgeRef Edge(Core.Regen.RegenResult res, double y, double z)
            {
                var e = res.State.Bodies[0].Solid.Topology.Edges.First(x => x.IsLinear && Math.Abs(x.Start.Y - y) < 1e-6 && Math.Abs(x.Start.Z - z) < 1e-6
                    && Math.Abs(x.End.Y - y) < 1e-6 && Math.Abs(x.End.Z - z) < 1e-6);
                return new Core.Model.EdgeRef { A = e.FaceA, B = e.FaceB, Hint = e.Midpoint };
            }
            doc.AddFeature(new Core.Model.FilletFeature { Radius = "3", Edges = { Edge(r, 0, 0), Edge(r, 60, 0) } });
            doc.AddFeature(new Core.Model.FilletFeature { Radius = "1", Edges = { Edge(r, 2, 2), Edge(r, 58, 2) } });
            var fp = (Core.Model.FlatPatternFeature)doc.AddFeature(new Core.Model.FlatPatternFeature());
            r = regen.Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            // folded: the flat pattern is computed but the part stays as it is
            var folded = KernelTests.Vex(r.State.Bodies[0].Solid);
            t.Eq(r.State.Flats.Count, 1);
            t.Eq(r.State.Flats.Values.First().Bends.Count, 2);
            t.True(r.State.Bodies[0].Solid.TightBounds().Size.Z > 30, "still folded");

            fp.Flatten = true;
            fp.KFactor = "0.5";
            r = regen.Rebuild(doc);
            t.True(r.Status[fp.Id].Ok, r.Status[fp.Id].Error ?? "");
            var size = r.State.Bodies[0].Solid.TightBounds().Size;
            t.Near(Math.Min(size.X, Math.Min(size.Y, size.Z)), 2, 1e-6);
            t.Eq(r.State.Bodies[0].Solid.Native!.Faces.Count, 6);
            t.Near(KernelTests.Vex(r.State.Bodies[0].Solid), 100 * 2 * (128 + 2 * Math.PI), 1e-3);
            t.Near(KernelTests.Vex(r.State.Bodies[0].Solid), folded, 0.2); // K = 0.5 keeps the volume (the folded bends are faceted)

            // a K-factor that is not a fraction fails the feature
            fp.KFactor = "1.5";
            r = regen.Rebuild(doc);
            t.True(!r.Status[fp.Id].Ok, "K-factor 1.5 accepted");

            // the feature survives a save / load
            fp.KFactor = "0.3";
            var doc2 = Core.Model.CadDocument.FromJson(doc.ToJson());
            var fp2 = doc2.Features.OfType<Core.Model.FlatPatternFeature>().Single();
            t.True(fp2.Flatten && fp2.KFactor == "0.3", "round trip");
            r = new Core.Regen.Regenerator().Rebuild(doc2);
            t.True(r.Status[fp2.Id].Ok, r.Status[fp2.Id].Error ?? "");
            t.Near(KernelTests.Vex(r.State.Bodies[0].Solid), 100 * 2 * (128 + Math.PI * 1.6), 1e-3);
        });

        t.Test("Recognize: B-spline planes, cylinders, lines and circles", () =>
        {
            const double tol = 1e-7;
            // a bilinear patch in a tilted plane
            var tilt = new Vec3(0, 1, 1).Normalized;
            var plane = new NurbsSurface(1, 1, new[,] { { Vec3.Zero, tilt * 10 }, { new Vec3(20, 0, 0), new Vec3(20, 0, 0) + tilt * 10 } }, null, new double[] { 0, 0, 1, 1 }, new double[] { 0, 0, 1, 1 });
            var ps = NRecognize.Surface(plane, tol, out var flip);
            t.True(ps?.Kind == SurfaceKind.Plane && !flip, "plane");
            // a quarter cylinder r = 5 about Z through (1, 2, 0): rational quadratic around, linear along
            var c = new Vec3(1, 2, 0); const double r = 5, w = 0.7071067811865476;
            Vec3[,] Quarter(double sign) => new[,]
            {
                { c + new Vec3(r, 0, 0), c + new Vec3(r, 0, 10) },
                { c + new Vec3(r, r * sign, 0), c + new Vec3(r, r * sign, 10) },
                { c + new Vec3(0, r * sign, 0), c + new Vec3(0, r * sign, 10) },
            };
            var weights = new double[,] { { 1, 1 }, { w, w }, { 1, 1 } };
            foreach (var sign in new[] { 1.0, -1.0 })
            {
                var cyl = new NurbsSurface(2, 1, Quarter(sign), weights, new double[] { 0, 0, 0, 1, 1, 1 }, new double[] { 0, 0, 1, 1 });
                var cs = NRecognize.Surface(cyl, tol, out flip);
                t.True(cs?.Kind == SurfaceKind.Cylinder, "cylinder");
                t.Near(cs!.R, r, 1e-9);
                t.Near(Vec3.Cross(cs.Axis, Vec3.UnitZ).Length, 0, 1e-9);
                var q = cs.Origin - c; t.Near((q - Vec3.UnitZ * q.Z).Length, 0, 1e-9);
                // the face keeps its side: the analytic normal (flipped when told) is the spline's
                var p = cyl.Eval(0.5, 0.5);
                var n = cs.Normal(p) * (flip ? -1 : 1);
                t.True(Vec3.Dot(n, cyl.Normal(0.5, 0.5)) > 0.999999, "same side");
            }
            // the same patch without weights is not round
            var bulge = new NurbsSurface(2, 1, Quarter(1), null, new double[] { 0, 0, 0, 1, 1, 1 }, new double[] { 0, 0, 1, 1 });
            t.True(NRecognize.Surface(bulge, tol, out _) == null, "a polynomial quarter is not a cylinder");

            // curves: a straight cubic, a rational quarter circle (both directions), a wavy cubic
            var line = new BSplineCurve(3, new[] { Vec3.Zero, new Vec3(1, 1, 0), new Vec3(2, 2, 0), new Vec3(3, 3, 0) }, new double[] { 0, 0, 0, 0, 1, 1, 1, 1 });
            t.True(NRecognize.Curve(line.Eval, tol) is NLine, "line");
            Vec3 Arc(double u) { var a = u * Math.PI / 2; return c + new Vec3(Math.Cos(a), Math.Sin(a), 0) * r; }
            var arc = NRecognize.Curve(Arc, tol) as NCircle;
            t.True(arc != null, "circle");
            t.Near(arc!.R, r, 1e-9);
            t.True(Vec3.Dot(arc.Axis, Vec3.UnitZ) > 0.999999, "counter-clockwise about +Z");
            var back = NRecognize.Curve(u => Arc(1 - u), tol) as NCircle;
            t.True(back != null && Vec3.Dot(back.Axis, Vec3.UnitZ) < -0.999999, "clockwise arc: axis -Z");
            var wavy = new BSplineCurve(3, new[] { Vec3.Zero, new Vec3(1, 1, 0), new Vec3(2, -1, 0), new Vec3(3, 0, 0) }, new double[] { 0, 0, 0, 0, 1, 1, 1, 1 });
            t.True(NRecognize.Curve(wavy.Eval, tol) == null, "free-form curve");
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
