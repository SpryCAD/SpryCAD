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

        t.Test("Solver: straight slot keeps its sides parallel to the centerline", () =>
        {
            // built as the Straight Slot tool does
            var sk = new Sketch();
            var ca = sk.AddPoint(new Vec2(0, 0)); var cb = sk.AddPoint(new Vec2(30, 0));
            var axis = sk.Add(new SkLine { P1 = ca.Id, P2 = cb.Id, Construction = true });
            var p1 = sk.AddPoint(new Vec2(0, 5)); var p2 = sk.AddPoint(new Vec2(30, 5));
            var p3 = sk.AddPoint(new Vec2(30, -5)); var p4 = sk.AddPoint(new Vec2(0, -5));
            var top = sk.Add(new SkLine { P1 = p1.Id, P2 = p2.Id });
            var bot = sk.Add(new SkLine { P1 = p3.Id, P2 = p4.Id });
            var arcB = sk.Add(new SkArc { Center = cb.Id, Start = p3.Id, End = p2.Id });
            var arcA = sk.Add(new SkArc { Center = ca.Id, Start = p1.Id, End = p4.Id });
            foreach (var (l, arc) in new[] { (top, arcB), (bot, arcA), (top, arcA), (bot, arcB) })
                SketchSolver.CaptureSign(sk, sk.AddConstraint(CType.Tangent, l.Id, arc.Id));
            var par = new SkConstraint { Id = 900, Type = CType.Parallel, Refs = new[] { top.Id, axis.Id } };
            t.True(!SketchSolver.WouldOverDefine(sk, par), "parallel side is not implied by the tangencies alone");
            var c = sk.AddConstraint(CType.Parallel, top.Id, axis.Id);
            var second = new SkConstraint { Id = 901, Type = CType.Parallel, Refs = new[] { bot.Id, axis.Id } };
            t.True(SketchSolver.WouldOverDefine(sk, second), "the other side then follows");

            // drag one corner of a side: the slot must stay a slot (parallel sides, equal ends)
            var r = SketchSolver.Solve(sk, new Dictionary<int, Vec2> { [p2.Id] = new Vec2(32, 9) });
            t.True(r.Converged, "converged");
            var u = (sk.P(cb.Id) - sk.P(ca.Id)).Normalized;
            t.Near(Vec2.Cross(u, (sk.P(p2.Id) - sk.P(p1.Id)).Normalized), 0, 1e-6);
            t.Near(Vec2.Cross(u, (sk.P(p4.Id) - sk.P(p3.Id)).Normalized), 0, 1e-6);
            t.Near(Vec2.Distance(sk.P(ca.Id), sk.P(p1.Id)), Vec2.Distance(sk.P(cb.Id), sk.P(p2.Id)), 1e-6);
            _ = c;
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

        t.Test("Sketch relations to model geometry are associative (midpoint of edge, model center)", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60, "W") });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            var body = r.State.Bodies[0];
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 20), Normal = Vec3.UnitZ };
            var frame = Resolver.ResolvePlane(new PlaneRef { Face = top }, r.State);

            // what the sketcher does when a point is placed on the midpoint of the model's back top edge
            var backEdge = body.Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.Y - 60) < 1e-9 && Math.Abs(e.End.Y - 60) < 1e-9
                && Math.Abs(e.Start.Z - 20) < 1e-9 && Math.Abs(e.End.Z - 20) < 1e-9);
            var sk = new Sketch();
            var refLine = (SkLine)SketchReferences.Create(sk, frame, backEdge, body.Id, false, backEdge.Midpoint)!;
            var c = sk.AddPoint(frame.ToLocal(backEdge.Midpoint) + new Vec2(0.3, -0.2));
            sk.AddConstraint(CType.Midpoint, c.Id, refLine.Id);
            sk.Add(new SkCircle { Center = c.Id, R = 5 });
            sk.RemoveUnusedReferences();
            t.True(sk.Entities.Contains(refLine), "reference kept by its relation");
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = sk });
            r = regen.Rebuild(doc);
            t.True(r.Status[s2.Id].Ok, r.Status[s2.Id].Error ?? "");
            var res = r.State.Sketches[s2.Id];
            t.Near(Vec3.Distance(res.Frame.ToWorld(res.Sketch.P(c.Id)), new Vec3(50, 60, 20)), 0, 1e-6);

            // the model changes: the relation follows it
            doc.Equations[0].Expr = "120";
            r = regen.Rebuild(doc);
            res = r.State.Sketches[s2.Id];
            t.Near(Vec3.Distance(res.Frame.ToWorld(res.Sketch.P(c.Id)), new Vec3(60, 60, 20)), 0, 1e-6);

            // a reference whose point is shared by sketch geometry (snapped to a model center/vertex) is kept
            var sk2 = new Sketch();
            var vtx = (SkPointEntity)SketchReferences.Create(sk2, frame, backEdge, body.Id, true, backEdge.Start)!;
            var q = sk2.AddPoint(new Vec2(10, 10));
            sk2.Add(new SkLine { P1 = vtx.P, P2 = q.Id });
            sk2.RemoveUnusedReferences();
            t.True(sk2.Entities.Contains(vtx), "shared-point reference kept");
        });

        t.Test("Delete: dependents found through any reference", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var doc = CadDocument.FromJson(File.ReadAllText(Path.Combine(dir!, "tests", "FeatherCad.Tests", "models", "shelled-bracket.fcad")));
            string Id(string name) => doc.Features.First(f => f.Name == name).Id;
            var deps = doc.DependentsOf(new[] { Id("Hole1") }).Select(f => f.Name).ToList();
            t.True(deps.Contains("LPattern1"), "pattern of the hole: " + string.Join(",", deps));
            t.True(!deps.Contains("Hole2") && !deps.Contains("Fillet1"), "unrelated features: " + string.Join(",", deps));
            t.Eq(doc.DependentsOf(new[] { Id("Hole2") }).Count, 0);
            var all = doc.DependentsOf(new[] { Id("Boss-Extrude1") }).Select(f => f.Name).ToList();
            t.True(all.Contains("Fillet1") && all.Contains("Shell1") && all.Contains("Hole2"), "base feature: " + string.Join(",", all));
        });

        t.Test("Fillets running into neighbouring geometry: overflow, clear errors, never a hang", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var models = Path.Combine(dir!, "tests", "FeatherCad.Tests", "models");
            var json = File.ReadAllText(Path.Combine(models, "shelled-bracket.fcad"));
            // inside the shell: a 4 mm fillet around the boss foot runs past the 0.5 mm gap to the wall
            FilletFeature AddFillet(CadDocument d, bool overflow) => (FilletFeature)d.AddFeature(new FilletFeature
            { Radius = "4", Overflow = overflow, Edges = { new EdgeRef { A = "F9.I.F2.End", B = "F9.I.F8.1.F7.0.R.CB", Hint = new Vec3(70, 2.5, 18) } } });

            // overflow (default): trimmed by the wall, adds material, stays exact and closed
            var doc = CadDocument.FromJson(json);
            var before = KernelTests.Vex(new Regenerator().Rebuild(doc).State.Bodies[0].Solid);
            var fil = AddFillet(doc, true);
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[fil.Id].Ok, "overflow fillet: " + r.Status[fil.Id].Error);
            var body = r.State.Bodies[0].Solid;
            t.True(body.Native != null && body.Topology.OpenHalfEdges == 0, "native, closed");
            t.True(KernelTests.Vex(body) > before + 1, "material added");

            // without overflow: a clear, fast error naming the edge (highlighted)
            doc = CadDocument.FromJson(json);
            fil = AddFillet(doc, false);
            var sw = Stopwatch.StartNew();
            r = new Regenerator().Rebuild(doc);
            t.True(!r.Status[fil.Id].Ok && r.Status[fil.Id].Error!.Contains("does not fit"), "fit error: " + r.Status[fil.Id].Error);
            t.True(r.Status[fil.Id].ProblemEdges.Count > 0, "problem edges reported");
            t.True(sw.ElapsedMilliseconds < 3000, $"fast failure ({sw.ElapsedMilliseconds} ms)");

            // the user's case: all inner floor edges (walls, corner, tube, both bosses) in one 2 mm fillet
            doc = CadDocument.FromJson(File.ReadAllText(Path.Combine(models, "shelled-bracket-inner-fillet.fcad")));
            r = new Regenerator().Rebuild(doc);
            var last = doc.Features[^1];
            t.True(r.Status[last.Id].Ok, "inner floor fillet: " + r.Status[last.Id].Error);
            body = r.State.Bodies[0].Solid;
            t.True(body.Native != null && body.Topology.OpenHalfEdges == 0, "inner floor fillet native, closed");
            t.True(ExactBrep.Build(body).Warnings.Count == 0, "exact B-rep");

            // and if a bad case reaches the boolean anyway, the fallback is time-boxed
            var (oldFit, oldBudget) = (NativeBlend.CheckFit, Csg.FallbackSeconds);
            NativeBlend.CheckFit = false; Csg.FallbackSeconds = 1;
            try
            {
                doc = CadDocument.FromJson(json);
                fil = AddFillet(doc, false);
                sw.Restart();
                r = new Regenerator().Rebuild(doc);
                t.True(sw.ElapsedMilliseconds < 8000, $"bounded failure ({sw.ElapsedMilliseconds} ms)");
            }
            finally { NativeBlend.CheckFit = oldFit; Csg.FallbackSeconds = oldBudget; }
        });

        t.Test("Demo models rebuild, also with changed parameters, and export exact STEP", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "feathercad-tests", "demos");
            t.Eq(Demos.Verify(dir, volumes: false), 0);
        });

        t.Test("Reference planes: offset, through a vertex or sketch point, at an angle", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60, "W") });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            // parallel to Right through the box corner (W, 60, 20): the vertex is the end of an edge between named faces
            var corner = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.ThroughPoint, Base = PlaneRef.Right,
                Point = new PointRef { Vertex = new EdgeRef { A = e1.Id + ".End", B = e1.Id + ".S.L6", Hint = new Vec3(100, 60, 20) } },
            });
            // through a sketch point
            var sk = new Sketch(); var sp = sk.AddPoint(new Vec2(30, 40));
            sk.Add(new SkPointEntity { P = sp.Id });
            var ps = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            var thru = doc.AddFeature(new PlaneFeature { Mode = PlaneMode.ThroughPoint, Base = PlaneRef.Top, Point = new PointRef { Sketch = ps.Id, SketchPoint = sp.Id } });
            // Top plane tilted 30 degrees about X, then moved through the sketch point
            var tilt = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.Angle, Base = PlaneRef.Top, Axis = new DirRef { Axis = "X" }, Angle = "30",
                Point = new PointRef { Sketch = ps.Id, SketchPoint = sp.Id },
            });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            var pc = r.State.Planes[corner.Id];
            t.Near(Vec3.Dot(new Vec3(100, 60, 20) - pc.Origin, pc.Normal), 0, 1e-9);
            t.Near(Math.Abs(Vec3.Dot(pc.Normal, Vec3.UnitX)), 1, 1e-12);
            var pt = r.State.Planes[thru.Id];
            t.Near(Vec3.Dot(new Vec3(30, 40, 0) - pt.Origin, pt.Normal), 0, 1e-9);
            var pa = r.State.Planes[tilt.Id];
            t.Near(Vec3.Distance(pa.Normal, new Vec3(0, Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6))), 0, 1e-12);
            t.Near(Vec3.Dot(new Vec3(30, 40, 0) - pa.Origin, pa.Normal), 0, 1e-9);
            // normal to a sketch line at its end (the end is picked by the reference point)
            var ls = new Sketch(); var l0 = ls.AddPoint(new Vec2(10, 10)); var l1 = ls.AddPoint(new Vec2(40, 50));
            var line = ls.Add(new SkLine { P1 = l0.Id, P2 = l1.Id, Construction = true });
            var lsk = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = ls });
            var normal = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.NormalToCurve, Curve = new CurveRef { Sketch = lsk.Id, SketchEntity = line.Id },
                Point = new PointRef { Sketch = lsk.Id, SketchPoint = l1.Id },
            });
            r = regen.Rebuild(doc);
            t.True(r.Status[normal.Id].Ok, r.Status[normal.Id].Error ?? "");
            var pn = r.State.Planes[normal.Id];
            t.Near(Vec3.Distance(pn.Origin, new Vec3(40, 50, 0)), 0, 1e-9);
            t.Near(Vec3.Distance(pn.Normal, new Vec3(30, 40, 0).Normalized), 0, 1e-12);
            // normal to a model edge (the box's vertical edge at x=W, y=60) at its top end
            var en = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.NormalToCurve, Curve = new CurveRef { Edge = new EdgeRef { A = e1.Id + ".S.L5", B = e1.Id + ".S.L6", Hint = new Vec3(100, 60, 10) } },
                Point = new PointRef { Vertex = new EdgeRef { A = e1.Id + ".End", B = e1.Id + ".S.L6", Hint = new Vec3(100, 60, 20) } },
            });
            r = regen.Rebuild(doc);
            t.True(r.Status[en.Id].Ok, r.Status[en.Id].Error ?? "");
            var pe = r.State.Planes[en.Id];
            t.Near(Vec3.Distance(pe.Origin, new Vec3(100, 60, 20)), 0, 1e-9);
            t.Near(Math.Abs(Vec3.Dot(pe.Normal, Vec3.UnitZ)), 1, 1e-12);
            // the vertex plane follows the model
            doc.Equations[0].Expr = "130";
            r = regen.Rebuild(doc);
            pc = r.State.Planes[corner.Id];
            t.Near(Vec3.Dot(new Vec3(130, 60, 20) - pc.Origin, pc.Normal), 0, 1e-9);
        });

        t.Test("Start-up warm-up model rebuilds cleanly", () =>
        {
            // the warm-up part must exercise the real paths without errors (else it warms nothing)
            var r = Warmup.Run();
            foreach (var st in r.Status.Values) t.True(st.Ok && st.Warnings.Count == 0, $"warm-up feature: {st.Error} {string.Join(";", st.Warnings)}");
            t.True(r.State.Bodies.Count == 1 && r.State.Bodies[0].Solid.Native != null, "native warm-up body");
        });

        t.Test("Performance budget: model rebuilds", () =>
        {
            // budgets are ~3-4x the measured warm rebuild time: they catch regressions, not machine noise
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var cases = new (string file, double budgetMs)[]
            {
                (Path.Combine(dir!, "samples", "bracket.fcad"), 120),
                (Path.Combine(dir!, "tests", "FeatherCad.Tests", "models", "shelled-bracket.fcad"), 400),
            };
            foreach (var (file, budget) in cases)
            {
                var json = CadDocument.Load(file).ToJson(); // samples are containers
                double best = double.MaxValue;
                for (int i = 0; i < 6; i++)
                {
                    var doc = CadDocument.FromJson(json);
                    Csg.ClearCache(); // measure the computation, not boolean cache hits
                    var sw = Stopwatch.StartNew();
                    var r = new Regenerator().Rebuild(doc);
                    var ms = sw.Elapsed.TotalMilliseconds;
                    if (i >= 2) best = Math.Min(best, ms);
                    t.True(r.Status.Values.All(st => st.Ok), "rebuild ok");
                }
                Console.WriteLine($"      {Path.GetFileName(file)}: {best:0.0} ms (budget {budget} ms)");
                t.True(best < budget, $"{Path.GetFileName(file)} rebuild {best:0.0} ms exceeds budget {budget} ms");
            }
        });

        t.Test("Samples rebuild watertight", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            t.True(dir != null, "samples folder");
            // (files named Failed* are reproductions of failures, tested on their own)
            foreach (var file in Directory.GetFiles(Path.Combine(dir!, "samples"), "*.fcad").Where(f => !Path.GetFileName(f).StartsWith("Failed")))
            {
                var doc = CadDocument.Load(file);
                if (Environment.GetEnvironmentVariable("FC_TRACE") != null) FeatherCad.Kernel.Native.NBoolean.Trace = m => Console.WriteLine("T " + m);
                var r = new Regenerator().Rebuild(doc);
                FeatherCad.Kernel.Native.NBoolean.Trace = null;
                for (int i = 0; i < r.States.Count; i++)
                    foreach (var b in r.States[i].Bodies)
                        Console.WriteLine($"        after {doc.Features[i].Name}: open={b.Solid.Topology.OpenHalfEdges} native={b.Solid.Native != null}");
                foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{Path.GetFileName(file)} {f.Name}: {r.Status[f.Id].Error}");
                foreach (var b in r.State.Bodies)
                {
                    Console.WriteLine($"      {Path.GetFileName(file)}: open={b.Solid.Topology.OpenHalfEdges} native={b.Solid.Native != null} vol={b.Solid.Volume():0.###} ({Csg.LastFallbackReason})");
                    t.Eq(b.Solid.Topology.OpenHalfEdges, 0);
                    // the display mesh (also used for mass properties) must enclose the same volume
                    var vMesh = b.Mesh.Volume; var vExact = KernelTests.Vex(b.Solid);
                    t.True(Math.Abs(vMesh - vExact) < 2e-3 * Math.Abs(vExact), $"display mesh volume {vMesh} vs {vExact}");
                }
                foreach (var b in r.State.Bodies)
                {
                    if (Environment.GetEnvironmentVariable("FC_DUMP") != null && b.Solid.Native != null)
                        foreach (var f in b.Solid.Native.Faces)
                        {
                            var bx = f.Bounds();
                            Console.WriteLine($"        {f.Tag.Name} {f.Surface.Kind} R={f.Surface.R:0.###} axis={f.Surface.Axis} o={f.Surface.Origin} sense={f.Sense} box={bx.Min}-{bx.Max} loops={string.Join("/", f.Loops.Select(l => l.Coedges.Count))}");
                        }
                    var brep = ExactBrep.Build(b.Solid);
                    t.True(brep.Warnings.Count == 0, $"{Path.GetFileName(file)} exact: {string.Join("; ", brep.Warnings)}");
                }
                var outDir = Path.Combine(Path.GetTempPath(), "feathercad-tests");
                Directory.CreateDirectory(outDir);
                using (var fs = File.Create(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".step")))
                    StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), Path.GetFileNameWithoutExtension(file), 1.0);
            }
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
            t.Near(KernelTests.Vex(r.State.Bodies[0].Solid), 100 * 60 * 20 - Math.PI * 64 * 20, 1.5);

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
            t.True(r.State.Bodies[0].Solid.Native != null, "shell is native: " + Csg.LastFallbackReason);
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

        // a 100 x 50 x 20 block (x 0..100, y 0..20 up, z -50..0) chamfered on the given top edges
        (double removed, Kernel.Native.NBody body) Chamfered(ChamferFeature ch, Func<TopoEdge, bool> pick)
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = Rect(100, 50) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var r = new Regenerator().Rebuild(doc);
            var before = r.State.Bodies[0].Solid.Volume();
            foreach (var e in r.State.Bodies[0].Solid.Topology.Edges.Where(e => e.IsLinear && (e.FaceA == e1.Id + ".End" || e.FaceB == e1.Id + ".End")).Where(pick))
                ch.Edges.Add(new EdgeRef { A = e.FaceA, B = e.FaceB, Hint = e.Midpoint });
            doc.AddFeature(ch);
            r = new Regenerator().Rebuild(doc);
            if (!r.Status[ch.Id].Ok) throw new Exception(r.Status[ch.Id].Error);
            var solid = r.State.Bodies[0].Solid;
            t.Eq(solid.Topology.OpenHalfEdges, 0);
            return (before - solid.Volume(), solid.Native!);
        }
        static bool FrontEdge(TopoEdge e) => Math.Abs(e.Start.Z) < 1e-9 && Math.Abs(e.End.Z) < 1e-9;

        t.Test("Chamfer types: equal, distance-distance, angle-distance (volumes)", () =>
        {
            t.Near(Chamfered(new ChamferFeature { Distance = "2" }, FrontEdge).removed, 0.5 * 2 * 2 * 100, 1e-6);
            t.Near(Chamfered(new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "2", Distance2 = "4" }, FrontEdge).removed, 0.5 * 2 * 4 * 100, 1e-6);
            // 90 degree edge: the other leg is d * tan(angle)
            t.Near(Chamfered(new ChamferFeature { Type = ChamferType.AngleDistance, Distance = "2", Angle = "30" }, FrontEdge).removed,
                0.5 * 2 * 2 * Math.Tan(Math.PI / 6) * 100, 1e-6);
            t.Near(Chamfered(new ChamferFeature { Type = ChamferType.AngleDistance, Distance = "2", Angle = "45" }, FrontEdge).removed, 0.5 * 2 * 2 * 100, 1e-6);
        });

        t.Test("Chamfer flip puts the distances on the other faces", () =>
        {
            // on the top face 3 from the edge: inside the chamfer only when the 4 leg runs along the top
            var probe = new Vec3(50, 19.9, -3);
            var a = Chamfered(new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "2", Distance2 = "4" }, FrontEdge).body;
            var b = Chamfered(new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "2", Distance2 = "4", Flip = true }, FrontEdge).body;
            t.True(NativeBlend.PointInside(a, probe) != NativeBlend.PointInside(b, probe), "flip swaps the legs");
            // legs 4 and 4 tan 60 = 6.9: 5 along the top is inside only when the 6.9 leg runs along the top
            var probe2 = new Vec3(50, 19.9, -5);
            var c = Chamfered(new ChamferFeature { Type = ChamferType.AngleDistance, Distance = "4", Angle = "60" }, FrontEdge).body;
            var d = Chamfered(new ChamferFeature { Type = ChamferType.AngleDistance, Distance = "4", Angle = "60", Flip = true }, FrontEdge).body;
            t.True(NativeBlend.PointInside(c, probe2) != NativeBlend.PointInside(d, probe2), "flip swaps the reference face");
        });

        t.Test("Unequal chamfer around the top keeps its long leg on the same face", () =>
        {
            foreach (var flip in new[] { false, true })
            {
                var (removed, body) = Chamfered(new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "1", Distance2 = "3", Flip = flip }, _ => true);
                // 2 from each edge on the top face: removed on all four sides, or kept on all four
                var probes = new[] { new Vec3(50, 19.95, -2), new Vec3(50, 19.95, -48), new Vec3(2, 19.95, -25), new Vec3(98, 19.95, -25) };
                var inside = probes.Select(p => NativeBlend.PointInside(body, p)).ToList();
                t.True(inside.All(x => x == inside[0]), $"flip={flip}: same leg on every edge ({string.Join(",", inside)})");
                t.True(removed > 1.5 * 290 && removed < 1.5 * 300, $"removed {removed}");
            }
        });

        t.Test("Unequal chamfer on a hole rim", () =>
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = Rect(50, 50) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "12" });
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(25, 12, -25), Normal = Vec3.UnitY };
            var hs = new Sketch(); var hp = hs.AddPoint(new Vec2(25, 25)); hs.Add(new SkPointEntity { P = hp.Id });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
            doc.AddFeature(new HoleFeature { SketchId = s2.Id, Diameter = "10", ThroughAll = true });
            var r = new Regenerator().Rebuild(doc);
            var before = r.State.Bodies[0].Solid.Volume();
            var rim = r.State.Bodies[0].Solid.Topology.Edges.First(e => e.Closed && (e.FaceA == e1.Id + ".End" || e.FaceB == e1.Id + ".End"));
            var got = new List<double>();
            foreach (var flip in new[] { false, true, false })
            {
                var d = doc.Clone();
                var ch = d.AddFeature(got.Count == 2 ? new ChamferFeature { Distance = "1", Edges = { new EdgeRef { A = rim.FaceA, B = rim.FaceB, Hint = rim.Midpoint } } }
                    : new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "1", Distance2 = "2", Flip = flip, Edges = { new EdgeRef { A = rim.FaceA, B = rim.FaceB, Hint = rim.Midpoint } } });
                var rr = new Regenerator().Rebuild(d);
                t.True(rr.Status[ch.Id].Ok, rr.Status[ch.Id].Error ?? "");
                got.Add(before - rr.State.Bodies[0].Solid.Volume());
            }
            // Pappus: triangle with legs dr (radial, on the top) and dh (down the bore), centroid at R + dr/3
            double Ring(double dr, double dh) => 2 * Math.PI * (5 + dr / 3) * dr * dh / 2;
            // volumes are measured on the faceted bore, a few percent over the exact rings (the equal chamfer too)
            t.Near(got[2], Ring(1, 1), Ring(1, 1) * 0.08);
            got.RemoveAt(2);
            got.Sort();
            t.Near(got[0], Ring(1, 2), Ring(1, 2) * 0.05);
            t.Near(got[1], Ring(2, 1), Ring(2, 1) * 0.05);
            t.True(got[1] - got[0] > 1, "flip moves the long leg from the bore to the top face");
        });

        t.Test("Document: rounded corner then top fillets (user scenario)", () =>
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = Rect(80, 60) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "30" });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            var body = r.State.Bodies[0].Solid;
            // vertical edge (parallel to Y) at the far corner
            var vert = body.Topology.Edges.First(e => e.IsLinear && Math.Abs((e.End - e.Start).Normalized.Y) > 0.99 && e.Start.X > 79);
            var f1 = doc.AddFeature(new FilletFeature { Radius = "10", Edges = { new EdgeRef { A = vert.FaceA, B = vert.FaceB, Hint = vert.Midpoint } } });
            r = regen.Rebuild(doc);
            t.True(r.Status[f1.Id].Ok, r.Status[f1.Id].Error ?? "");
            var top = e1.Id + ".End";
            var edges = r.State.Bodies[0].Solid.Topology.Edges.Where(e => e.FaceA == top || e.FaceB == top)
                .Select(e => new EdgeRef { A = e.FaceA, B = e.FaceB, Hint = e.Midpoint }).ToList();
            var preTopo = r.State.Bodies[0].Solid.Topology;
            var f2 = doc.AddFeature(new FilletFeature { Radius = "3", Edges = edges });
            r = regen.Rebuild(doc);
            t.True(r.Status[f2.Id].Ok, r.Status[f2.Id].Error ?? "");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
            var full = r.State.Bodies[0].Solid.Volume();
            // tangent propagation: one straight edge next to the corner pulls in the arc and the next line
            // the 70 mm top edge shortened by the corner round
            var one = edges.First(er => preTopo.EdgesBetween(er.A, er.B).Any(te => te.IsLinear && Math.Abs(te.Length - 70) < 1e-6));
            var f3 = (FilletFeature)f2.Clone(); f3.Edges = new() { one };
            var doc2 = doc.Clone(); doc2.Features[^1] = f3;
            var r2 = new Regenerator().Rebuild(doc2);
            t.True(r2.Status[f3.Id].Ok, r2.Status[f3.Id].Error ?? "");
            t.True(r2.State.Bodies[0].Solid.Faces.Any(f => f.Tag.Surface.Kind == SurfaceKind.Torus), "propagated through the rounded corner");
            t.True(r2.State.Bodies[0].Solid.Volume() > full, "fewer edges than the full selection");
            var dir = Path.Combine(Path.GetTempPath(), "feathercad-tests");
            doc.Save(Path.Combine(dir, "corner.fcad"));
        });

        t.Test("Sketch dimensions to model edges are associative", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60, "W") });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            var body = r.State.Bodies[0];
            var topFace = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 10), Normal = Vec3.UnitZ };
            var frame = Resolver.ResolvePlane(new PlaneRef { Face = topFace }, r.State);
            // right edge of the top face (x = W) and bottom edge (y = 0)
            var right = body.Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.X - 100) < 1e-9 && Math.Abs(e.End.X - 100) < 1e-9 && Math.Abs(e.Start.Z - 10) < 1e-9);
            var bottom = body.Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.Y) < 1e-9 && Math.Abs(e.End.Y) < 1e-9 && Math.Abs(e.Start.Z - 10) < 1e-9 && Math.Abs(e.End.Z - 10) < 1e-9);
            var sk = new Sketch();
            var hp = sk.AddPoint(new Vec2(80, 20)); sk.Add(new SkPointEntity { P = hp.Id });
            var rl = (SkLine)SketchReferences.Create(sk, frame, right, body.Id, false, right.Midpoint)!;
            var blE = SketchReferences.Create(sk, frame, bottom, body.Id, false, bottom.Midpoint);
            t.True(blE is SkLine, $"bottom ref: {blE?.GetType().Name ?? "null"} edge {bottom.Start}->{bottom.End} {bottom.FaceA}/{bottom.FaceB} vs right {right.FaceA}/{right.FaceB}");
            var bl = (SkLine)blE!;
            var d1 = sk.AddConstraint(CType.Distance, hp.Id, rl.Id); SketchSolver.CaptureSign(sk, d1); d1.Expr = "15"; d1.Value = 15;
            var d2 = sk.AddConstraint(CType.Distance, hp.Id, bl.Id); SketchSolver.CaptureSign(sk, d2); d2.Expr = "12"; d2.Value = 12;
            var res = SketchSolver.Solve(sk);
            t.Eq(res.Status, SketchStatus.FullyDefined);
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = topFace }, Sketch = sk });
            var h = doc.AddFeature(new HoleFeature { SketchId = s2.Id, Diameter = "6" });
            r = regen.Rebuild(doc);
            t.True(r.Status[h.Id].Ok, r.Status[h.Id].Error ?? "");
            Vec3 HoleCenter(RegenResult rr) => rr.State.Bodies[0].Solid.Faces.First(f => f.Tag.Surface.Kind == SurfaceKind.Cylinder).Tag.Surface.Origin;
            t.Near(HoleCenter(r).X, 85, 1e-6); t.Near(HoleCenter(r).Y, 12, 1e-6);
            // the part grows: the hole follows the right edge
            doc.Equations[0].Expr = "130";
            r = regen.Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            t.Near(HoleCenter(r).X, 115, 1e-6); t.Near(HoleCenter(r).Y, 12, 1e-6);
            // round trip keeps the references
            var doc2 = CadDocument.FromJson(doc.ToJson());
            t.True(((SketchFeature)doc2.Find(s2.Id)!).Sketch.Entities.Count(e => e.IsReference) == 2, "references saved");
        });

        t.Test("Angle dimension measures the sector it is placed in", () =>
        {
            // like the reported case: top line runs right from the vertex, the diagonal is stored pointing back to it
            var sk = new Sketch();
            var v = sk.AddPoint(new Vec2(0, 10)); v.Fixed = true;
            var a = sk.AddPoint(new Vec2(40, 10)); a.Fixed = true;
            var d = sk.AddPoint(new Vec2(40, -14.6));
            var top = sk.Add(new SkLine { P1 = v.Id, P2 = a.Id });
            var diag = sk.Add(new SkLine { P1 = d.Id, P2 = v.Id });
            var c = new SkConstraint { Id = sk.NewId(), Type = CType.Angle, Refs = new[] { top.Id, diag.Id } };
            c.Flip = SketchSolver.ChooseAngleFlip(sk.P(a.Id) - sk.P(v.Id), sk.P(v.Id) - sk.P(d.Id), new Vec2(15, -3)); // label inside the acute sector
            var measured = SketchSolver.Measure(sk, c);
            t.Near(measured, Math.Atan2(24.6, 40) * 180 / Math.PI, 1e-6); // ~31.6, not ~148.4
            SketchSolver.CaptureSign(sk, c);
            c.Value = 45;
            sk.Constraints.Add(c);
            sk.AddConstraint(CType.Vertical, a.Id, d.Id);
            var r = SketchSolver.Solve(sk);
            t.True(r.Converged, "solved");
            t.Near(SketchSolver.Measure(sk, c), 45, 1e-6);
            t.Near(sk.P(d.Id).Y, 10 - 40, 1e-6); // diagonal now at 45 degrees below the top line
        });
    }
}
