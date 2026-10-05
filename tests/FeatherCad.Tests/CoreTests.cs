using System.Diagnostics;
using FeatherCad.Core;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Core.Sketching;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

static class CoreTests
{
    /// <summary>Rectangle sketch with corner at origin, fully dimensioned.</summary>
    public static Sketch Rect(double w, double h, string? wExpr = null)
    {
        var sk = new Sketch();
        var p1 = sk.Point(Sketch.OriginId)!;
        var p2 = sk.AddPoint(new Vec2(w, 0)); var p3 = sk.AddPoint(new Vec2(w, h)); var p4 = sk.AddPoint(new Vec2(0, h));
        var l1 = sk.Add(new SkLine { P1 = p1.Id, P2 = p2.Id });
        var l2 = sk.Add(new SkLine { P1 = p2.Id, P2 = p3.Id });
        var l3 = sk.Add(new SkLine { P1 = p3.Id, P2 = p4.Id });
        var l4 = sk.Add(new SkLine { P1 = p4.Id, P2 = p1.Id });
        sk.AddConstraint(CType.Horizontal, l1.Id); sk.AddConstraint(CType.Horizontal, l3.Id);
        sk.AddConstraint(CType.Vertical, l2.Id); sk.AddConstraint(CType.Vertical, l4.Id);
        var d1 = sk.AddConstraint(CType.Distance, l1.Id); d1.Expr = wExpr ?? w.ToString(System.Globalization.CultureInfo.InvariantCulture); d1.Value = w;
        var d2 = sk.AddConstraint(CType.Distance, l2.Id); d2.Expr = h.ToString(System.Globalization.CultureInfo.InvariantCulture); d2.Value = h;
        return sk;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Expressions", () =>
        {
            t.Near(Expr.Eval("2 + 3 * 4"), 14, 1e-12);
            t.Near(Expr.Eval("(2 + 3) * 4 ^ 2"), 80, 1e-12);
            t.Near(Expr.Eval("sin(30) * 2"), 1, 1e-12);
            t.Near(Expr.Eval("1in + 1mm"), 26.4, 1e-12);
            t.Near(Expr.Eval("W / 2", n => n == "W" ? 50 : null), 25, 1e-12);
            t.Near(Expr.Eval("-2^2"), -4, 1e-12);
        });

        t.Test("Solver: rectangle fully defined", () =>
        {
            var sk = Rect(80, 40);
            // perturb
            sk.Point(2)!.X = 50; sk.Point(3)!.Y = 70;
            var r = SketchSolver.Solve(sk);
            t.True(r.Converged, "converged");
            t.Eq(r.Status, SketchStatus.FullyDefined);
            t.Near(sk.Point(2)!.X, 80, 1e-6); t.Near(sk.Point(2)!.Y, 40, 1e-6); t.Near(sk.Point(3)!.X, 0, 1e-6);
        });

        t.Test("Solver: under defined + drag", () =>
        {
            var sk = new Sketch();
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(10, 1));
            var l = sk.Add(new SkLine { P1 = a.Id, P2 = b.Id });
            sk.AddConstraint(CType.Horizontal, l.Id);
            var r = SketchSolver.Solve(sk);
            t.Eq(r.Status, SketchStatus.UnderDefined);
            t.Near(sk.Point(a.Id)!.Y, sk.Point(b.Id)!.Y, 1e-9);
            SketchSolver.Solve(sk, new Dictionary<int, Vec2> { [b.Id] = new Vec2(20, 5) });
            t.Near(sk.Point(b.Id)!.X, 20, 1e-6);
            t.Near(sk.Point(a.Id)!.Y, 5, 1e-6);
        });

        t.Test("Solver: circle tangent to line, radius", () =>
        {
            var sk = new Sketch();
            var a = sk.AddPoint(new Vec2(-20, 0)); var b = sk.AddPoint(new Vec2(20, 0));
            var l = sk.Add(new SkLine { P1 = a.Id, P2 = b.Id });
            var c = sk.AddPoint(new Vec2(0, 7));
            var ci = sk.Add(new SkCircle { Center = c.Id, R = 3 });
            var tg = sk.AddConstraint(CType.Tangent, l.Id, ci.Id); SketchSolver.CaptureSign(sk, tg);
            var rad = sk.AddConstraint(CType.Radius, ci.Id); rad.Value = 5;
            var r = SketchSolver.Solve(sk);
            t.True(r.Converged, "converged");
            var lineY = sk.P(a.Id).Y;
            t.Near(Math.Abs(sk.P(c.Id).Y - lineY), 5, 1e-6);
        });

        t.Test("Solver: redundant constraint detection", () =>
        {
            var sk = Rect(80, 40);
            SketchSolver.Solve(sk);
            var extra = new SkConstraint { Id = 999, Type = CType.Distance, Refs = new[] { sk.Entities[2].Id }, Value = 80 };
            t.True(SketchSolver.WouldOverDefine(sk, extra), "length of opposite side is redundant");
        });

        t.Test("Profile: rectangle with circle hole", () =>
        {
            var sk = Rect(80, 40);
            var c = sk.AddPoint(new Vec2(40, 20));
            sk.Add(new SkCircle { Center = c.Id, R = 10 });
            SketchSolver.Solve(sk);
            var regs = ProfileExtractor.Extract(sk, out _);
            t.Eq(regs.Count, 1);
            t.Eq(regs[0].Holes.Count, 1);
        });

        t.Test("Part: extrude, cut on face, fillet, hole, pattern, shell", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60, "W") });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            t.True(r.Status.Values.All(s => s.Ok), string.Join(";", r.Status.Values.Select(s => s.Error)));
            t.Near(r.State.Bodies[0].Solid.Volume(), 100 * 60 * 20, 1e-6);

            // sketch on the top (end) face: circle, cut through all
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 20), Normal = Vec3.UnitZ };
            var sk2 = new Sketch();
            var cp = sk2.AddPoint(new Vec2(30, 30));
            sk2.Add(new SkCircle { Center = cp.Id, R = 8 });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = sk2 });
            var cut = doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, IsCut = true, End = EndCondition.ThroughAll, Reverse = true });
            r = regen.Rebuild(doc);
            t.True(r.Status[cut.Id].Ok, r.Status[cut.Id].Error ?? "");
            var n8 = SurfaceDef.SegmentsFor(8);
            var vol = 100 * 60 * 20 - 0.5 * n8 * 64 * Math.Sin(2 * Math.PI / n8) * 20;
            t.Near(r.State.Bodies[0].Solid.Volume(), vol, 1e-3);

            // fillet a vertical edge
            var edge = r.State.Bodies[0].Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.X - 100) < 1e-6 && Math.Abs(e.Start.Y - 60) < 1e-6);
            var fil = doc.AddFeature(new FilletFeature { Radius = "5", Edges = { new EdgeRef { A = edge.FaceA, B = edge.FaceB, Hint = edge.Midpoint } } });
            // hole on top face
            var hs = new Sketch(); var hp = hs.AddPoint(new Vec2(70, 30)); hs.Add(new SkPointEntity { P = hp.Id });
            var s3 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
            var hole = doc.AddFeature(new HoleFeature { SketchId = s3.Id, Type = HoleType.Counterbore, Diameter = "6.6", ThroughAll = true });
            // pattern the hole along -X
            var lp = doc.AddFeature(new LinearPatternFeature { Seeds = { hole.Id }, Direction = new DirRef { Axis = "Y" }, Spacing = "-20", Count = "2" });
            var sw = Stopwatch.StartNew();
            r = regen.Rebuild(doc);
            var ms1 = sw.ElapsedMilliseconds;
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            for (int i = 0; i < r.States.Count; i++)
                if (r.States[i].Bodies.Count > 0)
                    Console.WriteLine($"      after {doc.Features[i].Name}: open={r.States[i].Bodies[0].Solid.Topology.OpenHalfEdges} faces={r.States[i].Bodies[0].Solid.Faces.Count} warn={string.Join(";", r.Status[doc.Features[i].Id].Warnings)}");
            t.Eq(r.State.Bodies.Count, 1);
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);

            // shell from the bottom face
            var sh = doc.AddFeature(new ShellFeature { Thickness = "2", Faces = { new FaceRef { Name = e1.Id + ".Start", Hint = new Vec3(50, 30, 0), Normal = -Vec3.UnitZ } } });
            r = regen.Rebuild(doc);
            t.True(r.Status[sh.Id].Ok, r.Status[sh.Id].Error ?? "");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
            Console.WriteLine($"      full rebuild {ms1} ms, rebuilt {r.Rebuilt} feature(s) incrementally in {r.Milliseconds:0} ms");

            // change equation -> parametric update
            doc.Equations[0].Expr = "120";
            r = regen.Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name} after W=120: {r.Status[f.Id].Error}");
            t.Near(r.State.Bodies[0].Solid.Bounds.Size.X, 120, 1e-6);

            // save / load round trip
            var json = doc.ToJson();
            var doc2 = CadDocument.FromJson(json);
            t.Eq(doc2.Features.Count, doc.Features.Count);
            var r2 = new Regenerator().Rebuild(doc2);
            t.Near(r2.State.Bodies[0].Solid.Volume(), r.State.Bodies[0].Solid.Volume(), 1e-6);
            var dir = Path.Combine(Path.GetTempPath(), "feathercad-tests");
            Directory.CreateDirectory(dir);
            doc.Save(Path.Combine(dir, "demo.fcad"));
            using (var fs = File.Create(Path.Combine(dir, "demo.step"))) StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), "demo");
            using (var fs = File.Create(Path.Combine(dir, "demo.stl"))) StlWriter.Write(fs, r.State.Bodies.Select(b => b.Solid));
        });

        t.Test("Revolve feature", () =>
        {
            var doc = new CadDocument();
            var sk = new Sketch();
            var a = sk.AddPoint(new Vec2(0, -10)); var b = sk.AddPoint(new Vec2(0, 30));
            sk.Add(new SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
            var p1 = sk.AddPoint(new Vec2(10, 0)); var p2 = sk.AddPoint(new Vec2(20, 0)); var p3 = sk.AddPoint(new Vec2(20, 20)); var p4 = sk.AddPoint(new Vec2(10, 20));
            sk.Add(new SkLine { P1 = p1.Id, P2 = p2.Id }); sk.Add(new SkLine { P1 = p2.Id, P2 = p3.Id });
            sk.Add(new SkLine { P1 = p3.Id, P2 = p4.Id }); sk.Add(new SkLine { P1 = p4.Id, P2 = p1.Id });
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            var rv = doc.AddFeature(new RevolveFeature { SketchId = s.Id });
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[rv.Id].Ok, r.Status[rv.Id].Error ?? "");
            var v = r.State.Bodies[0].Solid.Volume();
            t.Near(v, Math.PI * (400 - 100) * 20, 60);
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
        });

        t.Test("Mirror, circular pattern, plane, combine", () =>
        {
            var doc = new CadDocument();
            // base plate 100x60x10
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
            // boss: 10x10 square at (10,10) extruded 5 on top face
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 10), Normal = Vec3.UnitZ };
            var sk = Rect(10, 10);
            foreach (var p in sk.Points) if (p.Id != Sketch.OriginId) { p.X += 10; p.Y += 10; }
            // free the rectangle from the origin: rebuild it unattached
            var sq = new Sketch();
            var a = sq.AddPoint(new Vec2(10, 10)); var b = sq.AddPoint(new Vec2(20, 10)); var c = sq.AddPoint(new Vec2(20, 20)); var d = sq.AddPoint(new Vec2(10, 20));
            sq.Add(new SkLine { P1 = a.Id, P2 = b.Id }); sq.Add(new SkLine { P1 = b.Id, P2 = c.Id }); sq.Add(new SkLine { P1 = c.Id, P2 = d.Id }); sq.Add(new SkLine { P1 = d.Id, P2 = a.Id });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = sq });
            var boss = doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, Depth = "5" });
            // mirror about a reference plane at x = 50 (Right plane offset 50)
            var pl = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Right, Offset = "50" });
            var mir = doc.AddFeature(new MirrorFeature { Seeds = { boss.Id }, Plane = new PlaneRef { Feature = pl.Id } });
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            t.Near(r.State.Bodies[0].Solid.Volume(), 60000 + 2 * 500, 1e-6);
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);

            // circular pattern of the boss around the Z axis through (50,30): use an axis via a cylinder hole
            var hs = new Sketch(); var hc = hs.AddPoint(new Vec2(50, 30)); hs.Add(new SkCircle { Center = hc.Id, R = 5 });
            var s3 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
            var cut = doc.AddFeature(new ExtrudeFeature { SketchId = s3.Id, IsCut = true, End = EndCondition.ThroughAll, Reverse = true });
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[cut.Id].Ok, r.Status[cut.Id].Error ?? "");
            var cylName = r.State.Bodies[0].Solid.Faces.First(f => f.Tag.Surface.Kind == SurfaceKind.Cylinder).Tag.Name;
            var cp = doc.AddFeature(new CircularPatternFeature { Seeds = { boss.Id }, Axis = new DirRef { Face = new FaceRef { Name = cylName, Hint = new Vec3(55, 30, 5) } }, Count = "4" });
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[cp.Id].Ok, r.Status[cp.Id].Error ?? "");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);

            // separate body + combine subtract
            var s4 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(20, 20) });
            var e4 = doc.AddFeature(new ExtrudeFeature { SketchId = s4.Id, Depth = "30", Merge = false });
            r = new Regenerator().Rebuild(doc);
            t.Eq(r.State.Bodies.Count, 2);
            var before = r.State.Bodies[0].Solid.Volume();
            var comb = doc.AddFeature(new CombineFeature { Op = CombineOp.Subtract, MainBody = r.State.Bodies[0].Id, ToolBodies = { e4.Id } });
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[comb.Id].Ok, r.Status[comb.Id].Error ?? "");
            t.Eq(r.State.Bodies.Count, 1);
            t.Near(r.State.Bodies[0].Solid.Volume(), before - 20 * 20 * 10 - 500, 1e-6); // tool also removes the boss inside it
        });

        t.Test("Chamfer hole rim via document", () =>
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = Rect(50, 50) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "12" });
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(25, 12, -25), Normal = Vec3.UnitY };
            var hs = new Sketch(); var hp = hs.AddPoint(new Vec2(25, 25)); hs.Add(new SkPointEntity { P = hp.Id });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
            var h = doc.AddFeature(new HoleFeature { SketchId = s2.Id, Diameter = "10", ThroughAll = true });
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[h.Id].Ok, r.Status[h.Id].Error ?? "");
            var rim = r.State.Bodies[0].Solid.Topology.Edges.First(e => e.Closed && (e.FaceA == e1.Id + ".End" || e.FaceB == e1.Id + ".End"));
            var ch = doc.AddFeature(new ChamferFeature { Distance = "1", Edges = { new EdgeRef { A = rim.FaceA, B = rim.FaceB, Hint = rim.Midpoint } } });
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[ch.Id].Ok, r.Status[ch.Id].Error ?? "");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
        });
    }
}
