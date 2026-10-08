using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>Convert Entities: model edges into a sketch as exact lines and arcs (fitted ones for other curves).</summary>
static class SketchConvertTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Convert Entities: a rounded plate's face as exact lines and arcs, joined", () =>
        {
            // a 40 x 20 plate with its corners rounded R5, 10 thick
            var sk = CoreTests.Rect(40, 20);
            foreach (var corner in sk.Points.Select(p => p.Id).ToList()) t.True(SketchOps.Fillet(sk, corner, 5) == null, "corner rounded");
            var doc = new CadDocument();
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "10" });
            var body = new Regenerator().Rebuild(doc).State.Bodies.Single().Solid.Native!;
            body.Link();
            var top = body.Faces.Single(f => f.Surface.Kind == SurfaceKind.Plane && f.Coedges.All(c => Math.Abs(c.Edge.V0.P.Z - 10) < 1e-9));

            var into = new Sketch();
            foreach (var e in top.Coedges.Select(c => c.Edge)) t.True(SketchConvert.Edge(into, Frame.Front, e), "converted");
            t.Eq(into.Entities.OfType<SkLine>().Count(), 4);
            var arcs = into.Entities.OfType<SkArc>().ToList();
            t.Eq(arcs.Count, 4);
            // 8 ends shared by the lines and arcs, 4 centers, the origin: nothing doubled
            t.Eq(into.Points.Count, 13);
            var mid = new Vec2(20, 10);
            foreach (var a in arcs)
            {
                var (c, p0, p1) = (into.Point(a.Center)!.P, into.Point(a.Start)!.P, into.Point(a.End)!.P);
                t.Near(Vec2.Distance(c, p0), 5, 1e-9);
                t.Near(Vec2.Distance(c, p1), 5, 1e-9);
                // counter-clockwise from Start to End is the quarter round the plate's corner (not the 270° the other way)
                var a0 = Math.Atan2(p0.Y - c.Y, p0.X - c.X); var a1 = Math.Atan2(p1.Y - c.Y, p1.X - c.X);
                var sweep = a1 - a0; while (sweep <= 0) sweep += 2 * Math.PI;
                t.Near(sweep, Math.PI / 2, 1e-9);
                var m = c + new Vec2(Math.Cos(a0 + sweep / 2), Math.Sin(a0 + sweep / 2)) * 5;
                t.True(Vec2.Distance(m, mid) > Vec2.Distance(c, mid), "the arc bulges out at the corner");
            }
            t.True(into.Points.Where(p => p.Id != Sketch.OriginId).All(p => p.Fixed), "fixed");
            // converting again adds nothing
            t.True(top.Coedges.All(c => !SketchConvert.Edge(into, Frame.Front, c.Edge)), "already there");
            // Select chain: one straight edge brings the whole rounded outline (the sides' edges meet it square: not taken)
            var side = top.Coedges.Select(c => c.Edge).First(e => e.Curve is NLine);
            var chain = SketchConvert.TangentChain(body, side);
            t.Eq(chain.Count, 8);
            t.True(chain.All(e => top.Coedges.Any(c => c.Edge == e)), "the top outline only");
        });

        t.Test("Convert Entities: converted geometry follows the model (On Edge), and makes a profile", () =>
        {
            // a plate W x 20, corners R5, 10 thick; its top outline converted into a second sketch and extruded from it
            var sk = CoreTests.Rect(40, 20, "W");
            foreach (var corner in sk.Points.Select(p => p.Id).ToList()) SketchOps.Fillet(sk, corner, 5);
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "40" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            var plate = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
            var regen = new Regenerator();
            var body = regen.Rebuild(doc).State.Bodies.Single();
            var nb = body.Solid.Native!; nb.Link();
            var top = nb.Faces.Single(f => f.Surface.Kind == SurfaceKind.Plane && f.Coedges.All(c => Math.Abs(c.Edge.V0.P.Z - 10) < 1e-9));
            var conv = new Sketch();
            foreach (var c in top.Coedges) SketchConvert.Edge(conv, Frame.Front, c.Edge, body.Id);
            t.True(conv.Entities.All(e => e.Link != null), "linked");
            var s2 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = conv });

            double MaxX(RegenResult r) => r.State.Sketches[s2.Id].Sketch.Points.Max(p => p.X);
            var r1 = regen.Rebuild(doc);
            t.Near(MaxX(r1), 40, 1e-9);
            t.Eq(r1.State.Sketches[s2.Id].Regions.Count, 1);

            // wider: the converted outline goes with it, still a closed profile with its arcs R5
            doc.Equations[0].Expr = "60";
            var r2 = regen.Rebuild(doc);
            t.True(r2.Status.Values.All(s => s.Error == null), "rebuilt");
            var moved = r2.State.Sketches[s2.Id];
            t.Near(MaxX(r2), 60, 1e-9);
            t.Eq(moved.Regions.Count, 1);
            foreach (var a in moved.Sketch.Entities.OfType<SkArc>())
                t.Near(Vec2.Distance(moved.Sketch.Point(a.Center)!.P, moved.Sketch.Point(a.Start)!.P), 5, 1e-9);
            t.True(!r2.Status[s2.Id].Warnings.Any(), "no warnings: " + string.Join("; ", r2.Status[s2.Id].Warnings));

            // taken off the edges: stays where it was saved
            var free = (Sketch)conv.Clone();
            foreach (var e in free.Entities.ToList()) free.Unlink(e);
            t.True(free.Entities.All(e => e.Link == null) && free.Points.Where(p => p.Id != Sketch.OriginId).All(p => !p.Fixed), "free");
            GC.KeepAlive(plate);
        });

        t.Test("Convert Entities: a hole's rim as a circle; a tilted circle as fitted arcs within tolerance", () =>
        {
            var into = new Sketch();
            var v = new NVertex(new Vec3(13, 4, 2));
            var rim = new NEdge(new NCircle(new Vec3(10, 4, 2), new Vec3(0, 0, -1), Vec3.UnitX, 3), 0, 2 * Math.PI, v, v);
            t.True(SketchConvert.Edge(into, Frame.Front, rim), "rim");
            var circle = into.Entities.OfType<SkCircle>().Single();
            t.Near(circle.R, 3, 1e-12);
            t.True(Vec2.Distance(into.Point(circle.Center)!.P, new Vec2(10, 4)) < 1e-12, "center");

            // a circle tilted 40° to the sketch looks like an ellipse: arcs within the tolerance of it
            var tilted = new Sketch();
            var axis = new Vec3(Math.Sin(40 * Math.PI / 180), 0, Math.Cos(40 * Math.PI / 180));
            var c = new NCircle(Vec3.Zero, axis, Vec3.UnitY, 20);
            var w = new NVertex(c.Eval(0));
            var e = new NEdge(c, 0, 2 * Math.PI, w, w);
            t.True(SketchConvert.Edge(tilted, Frame.Front, e), "tilted");
            var fitted = tilted.Entities.OfType<SkArc>().ToList();
            t.True(fitted.Count is > 0 and < 64 && tilted.Entities.All(x => x is SkArc or SkLine), $"{fitted.Count} arcs");
            for (int i = 0; i < 360; i++)
            {
                var p = Frame.Front.ToLocal(c.Eval(i * Math.PI / 180));
                var d = fitted.Min(a =>
                {
                    var cc = tilted.Point(a.Center)!.P;
                    return Math.Abs(Vec2.Distance(p, cc) - Vec2.Distance(tilted.Point(a.Start)!.P, cc));
                });
                t.True(d <= SketchConvert.Tolerance * 1.5, $"within tolerance at {i}°: {d:g3}");
            }
        });
    }
}
