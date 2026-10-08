using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>Sweeps along lines and arcs: valid closed bodies whose exact volume is the profile area times the path length (Pappus).</summary>
static class SweepTests
{
    static NSweep.Seg Line(Vec3 a, Vec3 b) => new(a, b);
    // an arc in the XY plane from a, turning by angle about center (positive: counter-clockwise seen from +Z)
    static NSweep.Seg Arc(Vec3 a, Vec3 center, double angle)
    {
        var axis = angle > 0 ? Vec3.UnitZ : -Vec3.UnitZ;
        return new(a, Transform.Rotation(center, axis, Math.Abs(angle)).Point(a), center, axis, Math.Abs(angle));
    }

    static void Check(TestRunner t, NBody b, double volume, string what)
    {
        t.Eq(b.Validate(), null);
        var mp = NMass.Of(b);
        t.True(mp != null, $"{what}: integrable");
        t.True(Math.Abs(mp!.Volume - volume) < 1e-9 * volume, $"{what}: volume {mp.Volume:R} vs {volume:R}");
        // the display tessellation closes up (no cracks where the pieces were stitched)
        t.Eq(NTess.ToSolid(b).Topology.OpenHalfEdges, 0);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Sweep: a rectangle along a line, a fillet arc and a line (Pappus volume)", () =>
        {
            // path in XY: (0,0,0) -> (50,0,0), arc r 20 about (50,20,0) through 90°, then up to (70,70,0)
            var path = new List<NSweep.Seg> { Line(Vec3.Zero, new Vec3(50, 0, 0)), Arc(new Vec3(50, 0, 0), new Vec3(50, 20, 0), Math.PI / 2), Line(new Vec3(70, 20, 0), new Vec3(70, 70, 0)) };
            // profile 8 × 6 centred on the path start, in the plane x = 0 (normal +X = the start tangent)
            var frame = new Frame(Vec3.Zero, Vec3.UnitY, Vec3.UnitZ, Vec3.UnitX);
            var rect = new ProfileRegion(ProfileLoop.Polygon(new[] { new Vec2(-4, -3), new Vec2(4, -3), new Vec2(4, 3), new Vec2(-4, 3) }, "L"));
            var b = NSweep.Sweep(new[] { rect }, frame, path, "W");
            double len = 50 + 20 * Math.PI / 2 + 50;
            Check(t, b, 48 * len, "rectangle");
            t.True(b.Faces.Any(f => f.Tag.Name == "W.Start") && b.Faces.Any(f => f.Tag.Name == "W.End"), "start and end caps");
            t.True(b.Faces.Any(f => f.Surface.Kind == SurfaceKind.Cylinder), "the arc's outer side is a cylinder");
        });

        t.Test("Sweep: a circle around a closed rounded rectangle (a ring of tori and cylinders)", () =>
        {
            // a 60 × 40 rectangle with corners rounded r 10, counter-clockwise in XY, starting mid-bottom
            var path = new List<NSweep.Seg>
            {
                Line(new Vec3(0, 0, 0), new Vec3(20, 0, 0)), Arc(new Vec3(20, 0, 0), new Vec3(20, 10, 0), Math.PI / 2),
                Line(new Vec3(30, 10, 0), new Vec3(30, 30, 0)), Arc(new Vec3(30, 30, 0), new Vec3(20, 30, 0), Math.PI / 2),
                Line(new Vec3(20, 40, 0), new Vec3(-20, 40, 0)), Arc(new Vec3(-20, 40, 0), new Vec3(-20, 30, 0), Math.PI / 2),
                Line(new Vec3(-30, 30, 0), new Vec3(-30, 10, 0)), Arc(new Vec3(-30, 10, 0), new Vec3(-20, 10, 0), Math.PI / 2),
                Line(new Vec3(-20, 0, 0), new Vec3(0, 0, 0)),
            };
            var frame = new Frame(Vec3.Zero, Vec3.UnitY, Vec3.UnitZ, Vec3.UnitX);
            var circle = new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 3, "C"));
            var b = NSweep.Sweep(new[] { circle }, frame, path, "R");
            double len = 2 * (40 + 20) + 2 * Math.PI * 10;
            Check(t, b, Math.PI * 9 * len, "ring");
            t.True(b.Faces.All(f => !f.Tag.Name.EndsWith(".Start") && !f.Tag.Name.EndsWith(".End")), "a closed path has no caps");
            t.True(b.Faces.Any(f => f.Surface.Kind == SurfaceKind.Torus), "tori around the corners");
        });

        t.Test("Sweep: a profile with a hole along a line and an arc; a sharp corner is refused", () =>
        {
            var path = new List<NSweep.Seg> { Line(Vec3.Zero, new Vec3(30, 0, 0)), Arc(new Vec3(30, 0, 0), new Vec3(30, -25, 0), -Math.PI / 3) };
            var frame = new Frame(Vec3.Zero, Vec3.UnitY, Vec3.UnitZ, Vec3.UnitX);
            var tube = new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, 5, "O"), new[] { ProfileLoop.Circle(Vec2.Zero, 3, "I") });
            var b = NSweep.Sweep(new[] { tube }, frame, path, "T");
            Check(t, b, Math.PI * (25 - 9) * (30 + 25 * Math.PI / 3), "tube");
            // two lines meeting at a right angle: no tangent continuity
            var corner = new List<NSweep.Seg> { Line(Vec3.Zero, new Vec3(30, 0, 0)), Line(new Vec3(30, 0, 0), new Vec3(30, 30, 0)) };
            bool refused = false;
            try { NSweep.Sweep(new[] { tube }, frame, corner, "X"); } catch (ArgumentException) { refused = true; }
            t.True(refused, "a sharp corner in the path is refused");
        });
    }
}

/// <summary>The Sweep feature: profile and path sketches in a document, boss and cut, saved and patterned.</summary>
static class SweepFeatureTests
{
    // a circle of radius r at the origin of a sketch (on Right: normal +X)
    static SpryCad.Core.Sketching.Sketch Circle(double r)
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        var c = sk.Point(SpryCad.Core.Sketching.Sketch.OriginId)!;
        sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = c.Id, R = r });
        return sk;
    }

    // on Top (sketch y is world -z): from the origin along +x 50, a 90° arc r 20, then 50 more
    static SpryCad.Core.Sketching.Sketch LArcPath()
    {
        var sk = new SpryCad.Core.Sketching.Sketch();
        var o = sk.Point(SpryCad.Core.Sketching.Sketch.OriginId)!;
        var a = sk.AddPoint(new Vec2(50, 0)); var c = sk.AddPoint(new Vec2(50, 20)); var b = sk.AddPoint(new Vec2(70, 20)); var e = sk.AddPoint(new Vec2(70, 70));
        sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = o.Id, P2 = a.Id });
        sk.Add(new SpryCad.Core.Sketching.SkArc { Center = c.Id, Start = a.Id, End = b.Id });
        sk.Add(new SpryCad.Core.Sketching.SkLine { P1 = b.Id, P2 = e.Id });
        return sk;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Sweep feature: a circle along a line, an arc and a line; saved and loaded; patterned", () =>
        {
            var doc = new SpryCad.Core.Model.CadDocument();
            var prof = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Right, Sketch = Circle(3) });
            var path = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = LArcPath() });
            var sw = doc.AddFeature(new SpryCad.Core.Model.SweepFeature { ProfileSketchId = prof.Id, PathSketchId = path.Id });
            t.Eq(sw.Name, "Sweep1");
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[sw.Id].Ok, r.Status[sw.Id].Error ?? "");
            t.Eq(r.Status[path.Id].Warnings.Count, 0); // the path is open by design
            double len = 50 + 20 * Math.PI / 2 + 50, vol = Math.PI * 9 * len;
            t.True(Math.Abs(KernelTests.Vex(r.State.Bodies[0].Solid) - vol) < 1e-9 * vol, "Pappus volume");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
            // the .sprt round trip keeps the feature
            var again = SpryCad.Core.Model.CadDocument.FromJson(doc.ToJson());
            t.True(again.Features.Last() is SpryCad.Core.Model.SweepFeature s2 && s2.PathSketchId == path.Id, "sweep saved");
            var r2 = new SpryCad.Core.Regen.Regenerator().Rebuild(again);
            t.True(Math.Abs(KernelTests.Vex(r2.State.Bodies[0].Solid) - vol) < 1e-9 * vol, "same volume after loading");
            // patterned: three copies 20 apart along Y (the path lies in the XZ plane; the tube is 6 thick)
            doc.AddFeature(new SpryCad.Core.Model.LinearPatternFeature { Seeds = { sw.Id }, Direction = new SpryCad.Core.Model.DirRef { Axis = "Y" }, Spacing = "20", Count = "3" });
            var r3 = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r3.Status[doc.Features.Last().Id].Ok, r3.Status[doc.Features.Last().Id].Error ?? "");
            t.True(Math.Abs(r3.State.Bodies.Sum(b => KernelTests.Vex(b.Solid)) - 3 * vol) < 1e-6 * vol, "three sweeps");
        });

        t.Test("Sweep feature: a cut sweep grooves a block; a path off the profile and a sharp corner are explained", () =>
        {
            var doc = new SpryCad.Core.Model.CadDocument();
            // a block 100 × 100 × 20 on Top (y from 0 up to 20), grooved along the path at y = 0 by a circle r 3
            var blk = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = CoreTests.Rect(100, 100) });
            var ex = doc.AddFeature(new SpryCad.Core.Model.ExtrudeFeature { SketchId = blk.Id, Depth = "20" });
            var prof = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Right, Sketch = Circle(3) });
            var path = doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = LArcPath() });
            var cut = doc.AddFeature(new SpryCad.Core.Model.SweepFeature { ProfileSketchId = prof.Id, PathSketchId = path.Id, IsCut = true });
            t.Eq(cut.Name, "Cut-Sweep1");
            var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
            t.True(r.Status[cut.Id].Ok, r.Status[cut.Id].Error ?? "");
            var block = KernelTests.Vex(new SpryCad.Core.Regen.Regenerator().Rebuild(doc, doc.Features.IndexOf(ex) + 1).State.Bodies[0].Solid);
            var after = KernelTests.Vex(r.State.Bodies[0].Solid);
            t.True(after < block - 100 && after > block - Math.PI * 9 * 200, $"material removed: {block - after:0.###}");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);

            // a path that does not start on the profile's plane
            var doc2 = new SpryCad.Core.Model.CadDocument();
            var p2 = doc2.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = Circle(3) });
            var q2 = doc2.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = LArcPath() });
            var s2 = doc2.AddFeature(new SpryCad.Core.Model.SweepFeature { ProfileSketchId = p2.Id, PathSketchId = q2.Id });
            var e2 = new SpryCad.Core.Regen.Regenerator().Rebuild(doc2).Status[s2.Id].Error ?? "";
            t.True(e2.Contains("perpendicular") || e2.Contains("start on"), "explained: " + e2);
            // two lines at a right angle
            var corner = new SpryCad.Core.Sketching.Sketch();
            var o = corner.Point(SpryCad.Core.Sketching.Sketch.OriginId)!;
            var a = corner.AddPoint(new Vec2(40, 0)); var b = corner.AddPoint(new Vec2(40, 40));
            corner.Add(new SpryCad.Core.Sketching.SkLine { P1 = o.Id, P2 = a.Id });
            corner.Add(new SpryCad.Core.Sketching.SkLine { P1 = a.Id, P2 = b.Id });
            var doc3 = new SpryCad.Core.Model.CadDocument();
            var p3 = doc3.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Right, Sketch = Circle(3) });
            var q3 = doc3.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Top, Sketch = corner });
            var s3 = doc3.AddFeature(new SpryCad.Core.Model.SweepFeature { ProfileSketchId = p3.Id, PathSketchId = q3.Id });
            var e3 = new SpryCad.Core.Regen.Regenerator().Rebuild(doc3).Status[s3.Id].Error ?? "";
            t.True(e3.Contains("tangent"), "explained: " + e3);
        });
    }
}
