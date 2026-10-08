using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>
/// Draft: the Draft feature (faces tapered about a neutral plane) and drafted extrudes (bosses and cuts), checked by
/// their exact volumes: a drafted block is a frustum (Simpson's rule is exact for it), a drafted round boss a cone.
/// </summary>
static class DraftTests
{
    static readonly double T5 = Math.Tan(5 * Math.PI / 180);

    /// <summary>The volume of a block w x d, h high, its sides drafted by angle t (tan): narrowing by 2 h t each way.</summary>
    static double Frustum(double w, double d, double h, double t)
    {
        double A(double z) => (w - 2 * t * z) * (d - 2 * t * z);
        return h / 6 * (A(0) + 4 * A(h / 2) + A(h));
    }

    static double Cone(double r1, double r2, double h) => Math.PI * h / 3 * (r1 * r1 + r1 * r2 + r2 * r2);

    static NBody Body(CadDocument doc)
    {
        var r = new Regenerator().Rebuild(doc);
        var failed = r.Status.Values.FirstOrDefault(s => !s.Ok);
        if (failed != null) throw new Exception(failed.Error);
        return r.State.Bodies.Single().Solid.Native!;
    }

    static double Volume(NBody b) => NMass.Of(b)!.Volume;

    static Sketch Circle(double x, double y, double r)
    {
        var sk = new Sketch();
        var c = sk.AddPoint(new Vec2(x, y));
        sk.Add(new SkCircle { Center = c.Id, R = r });
        return sk;
    }

    static FaceRef RefTo(NFace f)
    {
        var box = f.Bounds();
        var mid = (box.Min + box.Max) / 2;
        return new FaceRef { Name = f.Tag.Name, Hint = f.Surface.Kind == SurfaceKind.Plane ? f.Surface.Plane!.Project(mid) : mid };
    }

    public static void Run(TestRunner t)
    {
        t.Test("Draft feature: a block's four sides 5 degrees from its base (a frustum), the face names kept", () =>
        {
            var doc = new CadDocument();
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(40, 30) });
            var e = doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "20" });
            var before = Body(doc);
            var sides = before.Faces.Where(f => f.Surface.Kind == SurfaceKind.Plane && Math.Abs(f.Surface.Plane!.Normal.Z) < 1e-9).ToList();
            t.Eq(sides.Count, 4);
            var start = before.Faces.Single(f => f.Tag.Name == e.Id + ".Start");
            doc.AddFeature(new DraftFeature { Neutral = new PlaneRef { Face = RefTo(start) }, Faces = sides.Select(RefTo).ToList(), Angle = "5" });
            var b = Body(doc);
            t.Eq(b.Validate(), null);
            t.Near(Volume(b), Frustum(40, 30, 20, T5), 1e-6);
            t.True(before.Faces.Select(f => f.Tag.Name).OrderBy(x => x).SequenceEqual(b.Faces.Select(f => f.Tag.Name).OrderBy(x => x)), "the same face names");
            // each side now leans 5 degrees in towards the top
            foreach (var f in b.Faces.Where(f => sides.Any(x => x.Tag.Name == f.Tag.Name)))
            {
                var n = f.Surface.Plane!.Normal * (f.Sense ? 1 : -1);
                t.Near(Math.Asin(n.Z) * 180 / Math.PI, 5, 1e-9);
            }
            // reversed: pulled the other way, the block widens upwards
            ((DraftFeature)doc.Features[^1]).Reverse = true;
            t.Near(Volume(Body(doc)), Frustum(40, 30, 20, -T5), 1e-6);
        });

        t.Test("Drafted extrudes: a block narrowing and widening, a round boss (a cone), a tapered hole cut through a plate", () =>
        {
            var doc = new CadDocument();
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(40, 30) });
            var e = doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "20", Draft = "5" });
            t.Near(Volume(Body(doc)), Frustum(40, 30, 20, T5), 1e-6);
            e.DraftOutward = true;
            t.Near(Volume(Body(doc)), Frustum(40, 30, 20, -T5), 1e-6);
            // the other way along the sketch's normal: the same block below it
            e.DraftOutward = false; e.Reverse = true;
            t.Near(Volume(Body(doc)), Frustum(40, 30, 20, T5), 1e-6);

            var boss = new CadDocument();
            var c = boss.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Circle(0, 0, 10) });
            boss.AddFeature(new ExtrudeFeature { SketchId = c.Id, Depth = "20", Draft = "5" });
            var cone = Body(boss);
            t.True(cone.Faces.Any(f => f.Surface.Kind == SurfaceKind.Cone), "the round side is a cone");
            t.Near(Volume(cone), Cone(10, 10 - 20 * T5, 20), 1e-6);

            // a 60 x 60 x 10 plate, a 16 mm hole cut from its top down with a 5 degree draft: narrowing as it goes in
            var plate = new CadDocument();
            var p = plate.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(60, 60) });
            plate.AddFeature(new ExtrudeFeature { SketchId = p.Id, Depth = "10", Reverse = true });
            var h = plate.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Circle(30, 30, 8) });
            plate.AddFeature(new ExtrudeFeature { SketchId = h.Id, IsCut = true, Depth = "10", Reverse = true, Draft = "5" });
            var cut = Body(plate);
            t.Eq(cut.Validate(), null);
            t.Near(Volume(cut), 60 * 60 * 10 - Cone(8, 8 - 10 * T5, 10), 1e-6);
        });

        t.Test("Drafted extrude of a slot: flat sides tangent to round ends, drafted together (a tangent edge kept tangent)", () =>
        {
            // a slot 30 between centres, 8 in radius: at height z its profile is the slot offset inwards by z tan(5)
            double L = 30, r = 8, hgt = 15;
            var sk = new Sketch();
            var c1 = sk.AddPoint(new Vec2(0, 0)); var c2 = sk.AddPoint(new Vec2(L, 0));
            var a = sk.AddPoint(new Vec2(0, -r)); var bb = sk.AddPoint(new Vec2(L, -r)); var c = sk.AddPoint(new Vec2(L, r)); var d = sk.AddPoint(new Vec2(0, r));
            sk.Add(new SkLine { P1 = a.Id, P2 = bb.Id });
            sk.Add(new SkArc { Center = c2.Id, Start = bb.Id, End = c.Id });
            sk.Add(new SkLine { P1 = c.Id, P2 = d.Id });
            sk.Add(new SkArc { Center = c1.Id, Start = d.Id, End = a.Id });
            var doc = new CadDocument();
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = hgt.ToString(System.Globalization.CultureInfo.InvariantCulture), Draft = "5" });
            var body = Body(doc);
            t.Eq(body.Validate(), null);
            double Area(double z) { var rr = r - z * T5; return 2 * rr * L + Math.PI * rr * rr; }
            t.Near(Volume(body), hgt / 6 * (Area(0) + 4 * Area(hgt / 2) + Area(hgt)), 1e-6);
            t.Eq(body.Faces.Count(f => f.Surface.Kind == SurfaceKind.Cone), 2);
        });

        t.Test("Drafted Through All cut: through the plate and no further (the walls taken far would cross); a hole that closes up first says so", () =>
        {
            CadDocument Plate(double r, string angle)
            {
                var plate = new CadDocument();
                var p = plate.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(60, 60) });
                plate.AddFeature(new ExtrudeFeature { SketchId = p.Id, Depth = "10", Reverse = true });
                var h = plate.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Circle(30, 30, r) });
                plate.AddFeature(new ExtrudeFeature { SketchId = h.Id, IsCut = true, End = EndCondition.ThroughAll, Reverse = true, Draft = angle });
                return plate;
            }
            var cut = Body(Plate(8, "5"));
            t.Eq(cut.Validate(), null);
            t.Near(Volume(cut), 60 * 60 * 10 - Cone(8, 8 - 10 * T5, 10), 1e-6);
            // a 2 mm hole at 20 degrees closes up 5.5 mm in, before the far side 10 mm down
            var doc = Plate(2, "20");
            var r = new Regenerator().Rebuild(doc);
            var st = r.Status[doc.Features[^1].Id];
            t.True(st is { Ok: false } && st.Error!.Contains("meet before the end"), $"closes up: {st.Error}");
        });

        t.Test("Draft: what cannot be drafted says why (a face parallel to the neutral plane; a draft through a mid-plane extrude)", () =>
        {
            var doc = new CadDocument();
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(40, 30) });
            var e = doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "20" });
            var b = Body(doc);
            var top = b.Faces.Single(f => f.Tag.Name == e.Id + ".End");
            var d = doc.AddFeature(new DraftFeature { Neutral = PlaneRef.Front, Faces = { RefTo(top) }, Angle = "5" });
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[d.Id] is { Ok: false } st && st.Error!.Contains("parallel to the neutral plane"), $"parallel face: {r.Status[d.Id].Error}");
            doc.Features.Remove(d);
            e.End = EndCondition.MidPlane; e.Draft = "5";
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[e.Id] is { Ok: false } st2 && st2.Error!.Contains("one direction"), $"mid plane: {r.Status[e.Id].Error}");
        });
    }
}
