using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Planes from up to three references and a relation to each, as SolidWorks' plane.</summary>
static class PlaneReferenceTests
{
    static PlaneReference On(PlaneRef p, PlaneRelation rel, string value = "10", bool flip = false) => new() { Plane = p, Relation = rel, Value = value, Flip = flip };

    public static void Run(TestRunner t)
    {
        void N(double got, double want, double tol, string what) => t.True(Math.Abs(got - want) <= tol, $"{what}: expected {want}, got {got}");

        t.Test("Reference planes: a relation to each reference, fully defined when all three freedoms are taken", () =>
        {
            var doc = new CadDocument();
            // on Front: a point at (3, 4), a line along X from (0, 0) to (10, 0), and two more points for three
            var sk = new Sketch();
            var pt = sk.AddPoint(new Vec2(3, 4));
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(10, 0));
            var line = sk.Add(new SkLine { P1 = a.Id, P2 = b.Id });
            var skf = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            // a Ø10 cylinder 20 long along Z at (40, 0)
            var cs = new Sketch();
            var cc = cs.AddPoint(new Vec2(40, 0));
            cs.Add(new SkCircle { Center = cc.Id, R = 5 });
            var csf = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = cs });
            doc.AddFeature(new ExtrudeFeature { SketchId = csf.Id, Depth = "20" });
            var regen = new Regenerator();
            var st0 = regen.Rebuild(doc).State;
            var cyl = st0.Bodies[0].Solid.Native!.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder);
            var cylRef = new FaceRef { Name = cyl.Tag.Name, Body = st0.Bodies[0].Id, Hint = new Vec3(45, 0, 10) };

            PointRef P(Vec2 v) => new() { Sketch = skf.Id, SketchPoint = v == new Vec2(3, 4) ? pt.Id : v == default ? a.Id : b.Id };
            Frame Solve(string what, params PlaneReference[] refs)
            {
                var pf = doc.AddFeature(new PlaneFeature { Name = what, Mode = PlaneMode.References, Refs = refs.ToList() });
                var r = regen.Rebuild(doc);
                t.True(r.Status[pf.Id].Ok, $"{what}: {r.Status[pf.Id].Error}");
                return r.State.Planes.GetValueOrDefault(pf.Id);
            }
            void Near(Vec3 got, Vec3 want, string what) => N(Vec3.Distance(got, want), 0, 1e-9, what);
            double Dist(Frame f, Vec3 p) => Vec3.Dot(p - f.Origin, f.Normal);

            // a face alone: offset from it
            var off = Solve("offset", On(PlaneRef.Front, PlaneRelation.Offset, "15"));
            Near(off.Normal, Vec3.UnitZ, "offset normal"); N(off.Origin.Z, 15, 1e-9, "offset");
            N(Solve("offset flipped", On(PlaneRef.Front, PlaneRelation.Offset, "15", flip: true)).Origin.Z, -15, 1e-9, "flipped offset");

            // parallel to Right through the point at (3, 4, 0)
            var thru = Solve("parallel", On(PlaneRef.Right, PlaneRelation.Parallel), new PlaneReference { Point = P(new Vec2(3, 4)) });
            Near(thru.Normal, Vec3.UnitX, "parallel normal"); N(thru.Origin.X, 3, 1e-9, "through the point");

            // perpendicular to a line alone: at its end nearest where it was picked
            var curve = new CurveRef { Sketch = skf.Id, SketchEntity = line.Id };
            var dir = new DirRef { Curve = curve };
            var end = Solve("normal at end", new PlaneReference { Line = dir, Curve = curve, Relation = PlaneRelation.Perpendicular, Hint = new Vec3(9, 0, 0) });
            N(Math.Abs(end.Normal.X), 1, 1e-9, "normal along the line"); N(end.Origin.X, 10, 1e-9, "at the end");
            // ... and through a point
            N(Solve("normal through point", new PlaneReference { Line = dir, Curve = curve, Relation = PlaneRelation.Perpendicular, Hint = new Vec3(9, 0, 0) },
                new PlaneReference { Point = P(new Vec2(3, 4)) }).Origin.X, 3, 1e-9, "through the point");

            // at 30° to Front, about the line along X
            var ang = Solve("angle", On(PlaneRef.Front, PlaneRelation.Angle, "30"), new PlaneReference { Line = dir, Curve = curve, Relation = PlaneRelation.Coincident });
            N(Math.Abs(Vec3.Dot(ang.Normal, Vec3.UnitZ)), Math.Cos(Math.PI / 6), 1e-9, "30° to Front");
            N(Math.Abs(Dist(ang, new Vec3(5, 0, 0))), 0, 1e-9, "through the line");

            // perpendicular to Top through the line: Front itself
            var perp = Solve("perpendicular", On(PlaneRef.Top, PlaneRelation.Perpendicular), new PlaneReference { Line = dir, Curve = curve, Relation = PlaneRelation.Coincident });
            N(Math.Abs(perp.Normal.Z), 1, 1e-9, "perpendicular to Top, through X"); N(Dist(perp, Vec3.Zero), 0, 1e-9, "through the line");

            // three points
            var three = Solve("three points", new PlaneReference { Point = P(new Vec2(3, 4)) }, new PlaneReference { Point = P(default) }, new PlaneReference { Point = P(new Vec2(10, 0)) });
            N(Math.Abs(three.Normal.Z), 1, 1e-9, "three points on Front");

            // midway between Front and a plane 20 in front of it
            var p20 = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Front, Offset = "20" });
            var mid = Solve("mid plane", On(PlaneRef.Front, PlaneRelation.MidPlane), On(new PlaneRef { Feature = p20.Id }, PlaneRelation.MidPlane));
            N(mid.Origin.Z, 10, 1e-9, "midway");
            // between Front and Right: the plane at 45° between them
            var bis = Solve("bisector", On(PlaneRef.Front, PlaneRelation.MidPlane), On(PlaneRef.Right, PlaneRelation.MidPlane));
            N(Math.Abs(Vec3.Dot(bis.Normal, Vec3.UnitX)), Math.Sqrt(0.5), 1e-9, "45° between");

            // tangent to the cylinder, parallel to Right, on the side picked (+X): x = 45; flipped: x = 35
            var tan = Solve("tangent", new PlaneReference { Round = cylRef, Line = new DirRef { Face = cylRef }, Relation = PlaneRelation.Tangent, Hint = new Vec3(45, 0, 10) }, On(PlaneRef.Right, PlaneRelation.Parallel));
            N(tan.Origin.X, 45, 1e-6, "tangent on the picked side");
            var tanF = Solve("tangent flipped", new PlaneReference { Round = cylRef, Line = new DirRef { Face = cylRef }, Relation = PlaneRelation.Tangent, Hint = new Vec3(45, 0, 10), Flip = true }, On(PlaneRef.Right, PlaneRelation.Parallel));
            N(tanF.Origin.X, 35, 1e-6, "tangent on the other side");
            // tangent through the point at (3, 4): the plane touches the cylinder, passes through the point
            var tp = Solve("tangent through point", new PlaneReference { Round = cylRef, Line = new DirRef { Face = cylRef }, Relation = PlaneRelation.Tangent, Hint = new Vec3(40, 5, 10) }, new PlaneReference { Point = P(new Vec2(3, 4)) });
            N(Math.Abs(Dist(tp, new Vec3(40, 0, 0))), 5, 1e-6, "touches the cylinder");
            N(Dist(tp, new Vec3(3, 4, 0)), 0, 1e-9, "through the point");
            N(tp.Normal.Z, 0, 1e-9, "along the axis");

            // under and over defined
            t.True(PlaneSolver.Defined([On(PlaneRef.Front, PlaneRelation.Parallel)]) == PlaneSolver.Definition.Under, "parallel alone is under defined");
            t.True(PlaneSolver.Defined([On(PlaneRef.Front, PlaneRelation.Offset), new PlaneReference { Point = P(default) }]) == PlaneSolver.Definition.Over, "offset and a point is over defined");
            var under = doc.AddFeature(new PlaneFeature { Mode = PlaneMode.References, Refs = [On(PlaneRef.Front, PlaneRelation.Parallel)] });
            var ru = regen.Rebuild(doc);
            t.True(!ru.Status[under.Id].Ok && ru.Status[under.Id].Error!.StartsWith("Under defined"), "says it is under defined");
            doc.Features.Remove(under);

            // picking: a face alone is offset from; a point after it makes it parallel through the point
            var refs = new List<PlaneReference> { new() { Plane = PlaneRef.Front } };
            PlaneSolver.Suggest(refs, refs[0]);
            t.True(refs[0].Relation == PlaneRelation.Offset, "face alone: offset");
            refs.Add(new PlaneReference { Point = P(default) });
            PlaneSolver.Suggest(refs, refs[1]);
            t.True(refs[0].Relation == PlaneRelation.Parallel && PlaneSolver.Defined(refs) == PlaneSolver.Definition.Full, "then a point: parallel through it");
            // a face then a line: perpendicular through the line
            refs = [new() { Plane = PlaneRef.Top }];
            PlaneSolver.Suggest(refs, refs[0]);
            refs.Add(new PlaneReference { Line = dir, Curve = curve });
            PlaneSolver.Suggest(refs, refs[1]);
            t.True(refs[0].Relation == PlaneRelation.Perpendicular && refs[1].Relation == PlaneRelation.Coincident, "then a line: perpendicular through it");
            // two faces: mid plane
            refs = [new() { Plane = PlaneRef.Front }];
            PlaneSolver.Suggest(refs, refs[0]);
            refs.Add(new PlaneReference { Plane = new PlaneRef { Feature = p20.Id } });
            PlaneSolver.Suggest(refs, refs[1]);
            t.True(refs.All(r => r.Relation == PlaneRelation.MidPlane), "two faces: mid plane");

            // saved and read again
            t.True(CadDocument.FromJson(doc.ToJson()).Features.OfType<PlaneFeature>().Any(p => p.Refs.Any(r => r.Round != null && r.Relation == PlaneRelation.Tangent)), "kept");
        });
    }
}
