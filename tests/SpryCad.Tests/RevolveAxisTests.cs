using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Revolve about any axis: a line of its sketch, X / Y / Z, or one from the model (a cylindrical or conical face).</summary>
static class RevolveAxisTests
{
    /// <summary>A w x h rectangle with its lower left corner at (x, y).</summary>
    static Sketch Box(double x, double y, double w, double h, bool centerline = false)
    {
        var sk = new Sketch();
        var p = new[] { sk.AddPoint(new Vec2(x, y)), sk.AddPoint(new Vec2(x + w, y)), sk.AddPoint(new Vec2(x + w, y + h)), sk.AddPoint(new Vec2(x, y + h)) };
        for (int i = 0; i < 4; i++) sk.Add(new SkLine { P1 = p[i].Id, P2 = p[(i + 1) % 4].Id });
        foreach (var q in p) q.Fixed = true;
        if (centerline)
        {
            var a = sk.AddPoint(new Vec2(0, -5), fix: true); var b = sk.AddPoint(new Vec2(0, 25), fix: true);
            sk.Add(new SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
        }
        return sk;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Revolve: about the Y axis, a centerline, a cylindrical face, a conical face; Z (not in the sketch) refused", () =>
        {
            // a ring 10..20 from the axis, 10 high, all the way round: pi (20² - 10²) 10
            var ring = Math.PI * (20 * 20 - 10 * 10) * 10;
            double Volume(RegenResult r) => r.State.Bodies.Sum(b => b.Solid.Volume());

            var doc = new CadDocument();
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Box(10, 0, 10, 10) });
            var rev = doc.AddFeature(new RevolveFeature { SketchId = s.Id, Axis = new DirRef { Axis = "Y" } });
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[rev.Id].Error == null, r.Status[rev.Id].Error ?? "");
            t.Near(Volume(r), ring, ring * 1e-3);

            // Z is not in the Front plane: no revolve about it
            rev.Axis = new DirRef { Axis = "Z" };
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[rev.Id].Error?.Contains("sketch's plane") == true, r.Status[rev.Id].Error ?? "no error");

            // the sketch's centerline, as before (none picked: the first one)
            var doc2 = new CadDocument();
            var s2 = doc2.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Box(10, 0, 10, 10, centerline: true) });
            doc2.AddFeature(new RevolveFeature { SketchId = s2.Id });
            t.Near(Volume(new Regenerator().Rebuild(doc2)), ring, ring * 1e-3);

            // about a cylinder's axis (a shaft R5 along Y), and a cone's (one revolved about Y): the ring around them
            foreach (var cone in new[] { false, true })
            {
                var d = new CadDocument();
                var shaftSk = cone ? new Sketch() : null;
                Feature shaft;
                if (cone)
                {
                    // a right triangle: base R5 at y = 0, apex on the axis at y = 30, revolved about the Y axis
                    var a = shaftSk!.AddPoint(new Vec2(0, 0), fix: true); var b = shaftSk.AddPoint(new Vec2(5, 0), fix: true); var c = shaftSk.AddPoint(new Vec2(0, 30), fix: true);
                    shaftSk.Add(new SkLine { P1 = a.Id, P2 = b.Id }); shaftSk.Add(new SkLine { P1 = b.Id, P2 = c.Id }); shaftSk.Add(new SkLine { P1 = c.Id, P2 = a.Id });
                    var ss = d.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = shaftSk });
                    shaft = d.AddFeature(new RevolveFeature { SketchId = ss.Id, Axis = new DirRef { Axis = "Y" } });
                }
                else
                {
                    var c = new Sketch();
                    var o = c.Point(Sketch.OriginId)!;
                    var ci = c.Add(new SkCircle { Center = o.Id, R = 5 });
                    var rc = c.AddConstraint(CType.Radius, ci.Id); rc.Value = 5; rc.Expr = "5";
                    var ss = d.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = c });
                    shaft = d.AddFeature(new ExtrudeFeature { SketchId = ss.Id, Depth = "30", Reverse = true });
                }
                var built = new Regenerator().Rebuild(d);
                t.True(built.Status[shaft.Id].Error == null, built.Status[shaft.Id].Error ?? "");
                var body = built.State.Bodies.Single();
                var kind = cone ? SurfaceKind.Cone : SurfaceKind.Cylinder;
                var face = body.Solid.Native!.Faces.First(f => f.Surface.Kind == kind);
                var hint = face.Coedges.First().Edge.Mid;
                var ringSk = d.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Box(10, 0, 10, 10) });
                var around = d.AddFeature(new RevolveFeature
                {
                    SketchId = ringSk.Id, Merge = false,
                    Axis = new DirRef { Face = new FaceRef { Name = face.Tag.Name, Body = body.Id, Hint = hint } },
                });
                var res = new Regenerator().Rebuild(d);
                t.True(res.Status[around.Id].Error == null, (cone ? "cone: " : "cylinder: ") + res.Status[around.Id].Error);
                var shaftVol = body.Solid.Volume();
                t.Near(Volume(res) - shaftVol, ring, ring * 1e-3);
            }
        });
    }
}
