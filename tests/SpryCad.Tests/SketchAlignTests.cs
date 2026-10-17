using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>A sketch's axes aligned to the model (Sketch Axes): its frame, relations along it, saving, a lost reference.</summary>
static class SketchAlignTests
{
    /// <summary>A line from the origin to (10, 3), Horizontal: it ends up along the sketch's X axis.</summary>
    static Sketch HorizontalLine(out long end)
    {
        var sk = new Sketch();
        var b = sk.AddPoint(new Vec2(10, 3));
        var l = sk.Add(new SkLine { P1 = Sketch.OriginId, P2 = b.Id });
        sk.AddConstraint(CType.Horizontal, l.Id);
        end = b.Id;
        return sk;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Sketch Axes: X along a direction, Horizontal along it, saved and loaded, a lost reference warns", () =>
        {
            var doc = new CadDocument();
            var s = doc.AddFeature(new SketchFeature
            {
                Plane = PlaneRef.Front, Sketch = HorizontalLine(out var end),
                Align = new SketchAlign { Dir = new DirRef { Axis = "Y" } },
            });
            var r = new Regenerator().Rebuild(doc);
            var res = r.State.Sketches[s.Id];
            t.True(Vec3.Distance(res.Frame.XDir, Vec3.UnitY) < 1e-12, $"X axis {res.Frame.XDir}");
            t.True(Vec3.Distance(res.Frame.YDir, -Vec3.UnitX) < 1e-12, $"Y axis {res.Frame.YDir}");
            t.True(Vec3.Distance(res.Frame.Normal, Vec3.UnitZ) < 1e-12, "the plane's normal kept");
            // Horizontal holds along the sketch's X axis: the line runs along the model's Y
            var w = res.Frame.ToWorld(res.Sketch.P(end));
            t.Near(w.X, 0, 1e-9); t.True(w.Y > 1, $"end at {w}");

            // flipped, and the Y axis along the direction instead
            ((SketchFeature)doc.Find(s.Id)!).Align!.Flip = true;
            t.True(Vec3.Distance(new Regenerator().Rebuild(doc).State.Sketches[s.Id].Frame.XDir, -Vec3.UnitY) < 1e-12, "flipped X axis");
            ((SketchFeature)doc.Find(s.Id)!).Align = new SketchAlign { Dir = new DirRef { Axis = "X" }, AlongY = true };
            var fy = new Regenerator().Rebuild(doc).State.Sketches[s.Id].Frame;
            t.True(Vec3.Distance(fy.YDir, Vec3.UnitX) < 1e-12 && Vec3.Distance(fy.XDir, -Vec3.UnitY) < 1e-12, $"Y along X: {fy.XDir} {fy.YDir}");

            // saved and loaded: the alignment kept; a sketch without one saves no "align"
            var back = CadDocument.FromJson(doc.ToJson());
            t.True(back.Find(s.Id) is SketchFeature { Align: { AlongY: true, Dir.Axis: "X" } }, "alignment loaded");
            var plain = new CadDocument();
            plain.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = HorizontalLine(out _) });
            t.True(!plain.ToJson().Contains("align", StringComparison.OrdinalIgnoreCase), "no alignment saved");

            // a direction normal to the plane, or one no longer there: the plane's axes, with a warning
            foreach (var dir in new[] { new DirRef { Axis = "Z" }, new DirRef { Feature = "gone" } })
            {
                ((SketchFeature)doc.Find(s.Id)!).Align = new SketchAlign { Dir = dir };
                var lost = new Regenerator().Rebuild(doc);
                t.True(Vec3.Distance(lost.State.Sketches[s.Id].Frame.XDir, Vec3.UnitX) < 1e-12, "the plane's X axis");
                t.True(lost.Status[s.Id].Warnings.Any(x => x.StartsWith("Could not align the sketch axes")), string.Join("; ", lost.Status[s.Id].Warnings));
            }
        });

        t.Test("Sketch Axes: a face at the top (Y along its normal); turning the sketch keeps it in place", () =>
        {
            // a 20 x 10 x 10 box on the Top plane; its +X side face
            var doc = new CadDocument();
            var bs = new Sketch();
            var p = new[] { bs.AddPoint(new Vec2(0, 0), true), bs.AddPoint(new Vec2(20, 0), true), bs.AddPoint(new Vec2(20, 10), true), bs.AddPoint(new Vec2(0, 10), true) };
            for (int i = 0; i < 4; i++) bs.Add(new SkLine { P1 = p[i].Id, P2 = p[(i + 1) % 4].Id });
            var boxSk = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = bs });
            doc.AddFeature(new ExtrudeFeature { SketchId = boxSk.Id, Depth = "10" });
            var built = new Regenerator().Rebuild(doc);
            var body = built.State.Bodies.Single();
            var side = body.Solid.Faces.First(f => !f.Tag.Surface.IsCurved && Vec3.Distance(f.Plane.Normal, Vec3.UnitX) < 1e-9);
            var face = new DirRef { Face = new FaceRef { Name = side.Tag.Name, Body = body.Id, Hint = side.Centroid() } };

            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = HorizontalLine(out _), Align = new SketchAlign { Dir = face, AlongY = true } });
            var r = new Regenerator().Rebuild(doc);
            t.True(!r.Status[s.Id].Warnings.Any(x => x.StartsWith("Could not align")), string.Join("; ", r.Status[s.Id].Warnings));
            var f = r.State.Sketches[s.Id].Frame;
            t.True(Vec3.Distance(f.YDir, Vec3.UnitX) < 1e-9, $"Y axis {f.YDir}");
            t.True(Vec3.Distance(Vec3.Cross(f.XDir, f.YDir), f.Normal) < 1e-12, "right-handed");

            // back to the plane's axes, Sketch.Rotate by minus the axes' turn: every point where it was in the model
            var plane = Resolver.ResolvePlane(PlaneRef.Top, r.State);
            var sk = r.State.Sketches[s.Id].Sketch.Clone();
            var before = sk.Points.Select(q => f.ToWorld(q.P)).ToList();
            var turn = Math.Atan2(Vec3.Dot(plane.XDir, f.YDir), Vec3.Dot(plane.XDir, f.XDir));
            t.True(Math.Abs(turn) > 0.1, "the axes turned");
            sk.Rotate(-turn);
            for (int i = 0; i < before.Count; i++) t.True(Vec3.Distance(plane.ToWorld(sk.Points[i].P), before[i]) < 1e-9, $"point {i} moved");
        });
    }
}
