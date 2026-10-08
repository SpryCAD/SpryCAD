using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Centers of circles and axes of round faces as references: in features (they follow the model) and in mates.</summary>
static class AxisCenterTests
{
    /// <summary>A 100 × 60 × 10 plate with a Ø10 hole whose center is at (X, 30).</summary>
    static (CadDocument doc, SketchFeature holeSketch, Feature cut) Plate(double x)
    {
        var doc = new CadDocument();
        var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60) });
        doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
        var sk = new Sketch();
        var c = sk.AddPoint(new Vec2(x, 30));
        sk.Add(new SkCircle { Center = c.Id, R = 5 });
        var s2 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        var cut = doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        return (doc, s2, cut);
    }

    /// <summary>The hole's circular edge on the plate's top face (z 10), by its faces.</summary>
    static EdgeRef TopRim(ModelState st)
    {
        var nb = st.Bodies[0].Solid.Native!;
        nb.Link();
        var e = nb.Edges.First(e => e.Curve is Kernel.Native.NCircle c && Math.Abs(c.C.Z - 10) < 1e-6 && e.Uses.Count == 2);
        return new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Body = st.Bodies[0].Id, Hint = e.Mid };
    }

    public static void Run(TestRunner t)
    {
        t.Test("Centers of circles as reference points: a plane through a hole's center follows the hole", () =>
        {
            var (doc, hs, _) = Plate(20);
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            var rim = TopRim(r.State);
            var at = Resolver.ResolvePoint(new PointRef { Center = rim }, r.State);
            t.Near(Vec3.Distance(at, new Vec3(20, 30, 10)), 0, 1e-9);

            var plane = doc.AddFeature(new PlaneFeature { Mode = PlaneMode.ThroughPoint, Base = PlaneRef.Right, Point = new PointRef { Center = rim } });
            r = regen.Rebuild(doc);
            t.True(r.Status[plane.Id].Ok, r.Status[plane.Id].Error ?? "");
            t.Near(r.State.Planes[plane.Id].Origin.X, 20, 1e-9);

            // the hole moves: the plane with it
            var hsk = hs.Sketch;
            hsk.Points.First(p => p.Id != Sketch.OriginId).P = new Vec2(70, 30);
            r = regen.Rebuild(doc);
            t.True(r.Status[plane.Id].Ok, r.Status[plane.Id].Error ?? "");
            t.Near(r.State.Planes[plane.Id].Origin.X, 70, 1e-9);

            // saved and read again
            t.True(CadDocument.FromJson(doc.ToJson()).Find(plane.Id) is PlaneFeature { Point.Center: not null }, "kept");
        });

        t.Test("Mates: a pin's end center onto a hole's center (round face ends as points), its axis on the hole's", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-axis-tests", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var (plate, _, _) = Plate(20);
            var pin = new CadDocument();
            var ps = new Sketch();
            ps.Add(new SkCircle { Center = Sketch.OriginId, R = 5 });
            var pss = pin.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = ps });
            pin.AddFeature(new ExtrudeFeature { SketchId = pss.Id, Depth = "20" });
            string pp = Path.Combine(dir, "Plate.sprt"), np = Path.Combine(dir, "Pin.sprt");
            plate.Save(pp); pin.Save(np);

            var m = AssemblyModel.New();
            var pc = m.Insert(pp, Transform.Identity);
            var nc = m.Insert(np, Transform.Translation(new Vec3(-40, 80, 15)) * Transform.Rotation(Vec3.Zero, new Vec3(1, 0.3, 0), 0.7));
            string Round(Component c) => m.DisplayOf(c)!.Mesh.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder).Name;
            string hole = Round(pc), side = Round(nc);

            // the ends of the hole: its centers on the plate's faces; the nearer one to a point found
            var top = m.RefTo(pc.Id, hole, m.NearerEnd(pc.Id, hole, new Vec3(20, 30, 11)))!;
            t.True(top is { Kind: MateGeomKind.Point, End: not null, R: 0 }, "a point, no radius");
            t.Near(Vec3.Distance(m.World(top).P, new Vec3(20, 30, 10)), 0, 1e-6);
            var bottom = m.RefTo(pc.Id, hole, -top.End)!;
            t.Near(Vec3.Distance(m.World(bottom).P, new Vec3(20, 30, 0)), 0, 1e-6);
            t.True(m.RefTo(pc.Id, m.DisplayOf(pc)!.Mesh.Faces.First(f => f.Surface.Kind == SurfaceKind.Plane).Name, 1) == null, "a flat face has no end center");
            t.True(m.AxisSegment(pc.Id, hole) is { } seg && Vec3.Distance(seg.a, seg.b) > 10, "the hole's axis, longer than the hole");

            // the pin's lower end center on the hole's top center, its axis on the hole's
            var pinStart = m.RefTo(nc.Id, side, m.NearerEnd(nc.Id, side, nc.Placement.ToTransform().Point(Vec3.Zero)))!;
            m.AddMate(MateType.Concentric, m.RefTo(nc.Id, side)!, m.RefTo(pc.Id, hole)!);
            m.AddMate(MateType.Coincident, pinStart, top);
            var res = m.SolveMates();
            t.True(m.Doc.Mates.All(x => (res.Errors.GetValueOrDefault(x.Id)) < 1e-6), "mates hold");
            t.Near(Vec3.Distance(m.World(pinStart).P, new Vec3(20, 30, 10)), 0, 1e-5);

            // saved and opened again: the center reference kept
            var ap = Path.Combine(dir, "Pinned.sasm");
            m.Save(ap);
            var back = AssemblyModel.Open(AssemblyDocument.Load(ap), ap);
            t.True(back.Doc.Mates.Any(x => x.A.End != null || x.B.End != null), "End saved");
            t.True(back.Doc.Mates.All(x => x.A.End == null || x.A.Kind == MateGeomKind.Point), "kinds kept");
        });
    }
}
